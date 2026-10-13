using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.Udon;
using VRC.Udon.Common.Interfaces;

namespace Synthos.SynSceneOptimizer
{
    /// <summary>
    /// Reads every Udon program the scene can run and works out which shader keywords they can set on materials and
    /// which material texture slots they can read. A texture in a slot that no variant samples can only be drawn if a
    /// script turns on a keyword that makes the shader sample it, or reads it out of the material.
    ///
    /// Udon programs are plain bytecode: PUSH heap-address, COPY, EXTERN "signature". The value passed to a keyword
    /// call is traced back through COPYs to the heap constants it can come from. Anything that can't be traced (a
    /// string built at runtime, a keyword array, an unreadable program) makes the result "unknown", which callers
    /// treat as "a script could set any keyword".
    /// </summary>
    public class SynUdonUsageScan
    {
        public bool KeywordsUnknown;
        public readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.Ordinal);
        public bool TextureReadsUnknown;
        public readonly HashSet<string> TextureReads = new HashSet<string>(StringComparer.Ordinal);
        public readonly List<string> UnknownReasons = new List<string>();
        public int ProgramCount;

        private const uint OpNop = 0, OpPush = 1, OpPop = 2, OpJumpIfFalse = 4, OpJump = 5, OpExtern = 6, OpAnnotation = 7, OpJumpIndirect = 8, OpCopy = 9;

        /// <summary>Scans the programs of every Udon behaviour in the scene and in prefabs/assets it references.</summary>
        public static SynUdonUsageScan Run(Scene scene)
        {
            var roots = new List<UnityEngine.Object>();
            foreach (GameObject root in scene.GetRootGameObjects()) roots.Add(root);
            return Run(EditorUtility.CollectDependencies(roots.ToArray()));
        }

        public static SynUdonUsageScan Run(UnityEngine.Object[] dependencies)
        {
            var scan = new SynUdonUsageScan();

            // Every behaviour per program, for the inspector values of its public variables
            var behavioursByProgram = new Dictionary<AbstractUdonProgramSource, List<UdonBehaviour>>();
            foreach (UnityEngine.Object obj in dependencies)
            {
                if (!(obj is UdonBehaviour udon) || udon.programSource == null) continue;
                if (!behavioursByProgram.TryGetValue(udon.programSource, out var list)) behavioursByProgram[udon.programSource] = list = new List<UdonBehaviour>();
                list.Add(udon);
            }

            var programs = new List<(AbstractUdonProgramSource source, IUdonProgram program)>();
            foreach (AbstractUdonProgramSource source in behavioursByProgram.Keys)
            {
                IUdonProgram program = null;
                try
                {
                    // UdonSharp keeps its compiled program in memory and writes the serialized copy when the SDK builds
                    program = (source as UdonSharp.UdonSharpProgramAsset)?.GetRealProgram();
                    if (program == null && source.SerializedProgramAsset != null) program = source.SerializedProgramAsset.RetrieveProgram();
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[SYN SCENE OPTIMIZER] Could not read Udon program '{source.name}': {e.Message}");
                }

                if (program == null || program.ByteCode == null || program.Heap == null)
                {
                    scan.MarkKeywordsUnknown($"could not read Udon program '{source.name}'");
                    scan.MarkTextureReadsUnknown($"could not read Udon program '{source.name}'");
                    continue;
                }
                programs.Add((source, program));
            }

            // Another program can overwrite any public variable at runtime with SetProgramVariable
            bool anySetProgramVariable = false;
            foreach (var (_, program) in programs)
            {
                foreach (string signature in GetExternSignatures(program))
                {
                    if (signature.Contains("__SetProgramVariable__")) anySetProgramVariable = true;
                }
            }

            foreach (var (source, program) in programs)
            {
                scan.ProgramCount++;
                new ProgramReader(scan, source, program, behavioursByProgram[source], anySetProgramVariable).Read();
            }
            return scan;
        }

        private void MarkKeywordsUnknown(string reason)
        {
            KeywordsUnknown = true;
            if (UnknownReasons.Count < 5 && !UnknownReasons.Contains(reason)) UnknownReasons.Add(reason);
        }

        private void MarkTextureReadsUnknown(string reason)
        {
            TextureReadsUnknown = true;
            if (UnknownReasons.Count < 5 && !UnknownReasons.Contains(reason)) UnknownReasons.Add(reason);
        }

        private static IEnumerable<string> GetExternSignatures(IUdonProgram program)
        {
            byte[] code = program.ByteCode;
            for (int pc = 0; pc + 4 <= code.Length;)
            {
                uint op = ReadWord(code, pc);
                if (op == OpExtern && pc + 8 <= code.Length && program.Heap.TryGetHeapVariable(ReadWord(code, pc + 4), out object sig) && sig is string text)
                    yield return text;
                pc += OperandCount(op) == 1 ? 8 : 4;
            }
        }

        private static uint ReadWord(byte[] code, int offset)
        {
            return (uint)(code[offset] << 24 | code[offset + 1] << 16 | code[offset + 2] << 8 | code[offset + 3]);
        }

        private static int OperandCount(uint op)
        {
            switch (op)
            {
                case OpPush:
                case OpJumpIfFalse:
                case OpJump:
                case OpExtern:
                case OpAnnotation:
                case OpJumpIndirect:
                    return 1;
                default:
                    return 0;
            }
        }

        private class ProgramReader
        {
            private readonly SynUdonUsageScan scan;
            private readonly AbstractUdonProgramSource source;
            private readonly IUdonProgram program;
            private readonly List<UdonBehaviour> behaviours;
            private readonly bool anySetProgramVariable;

            // Heap addresses each address can be copied from, and addresses an extern writes a result into
            private readonly Dictionary<uint, List<uint>> copySources = new Dictionary<uint, List<uint>>();
            private readonly HashSet<uint> externOutputs = new HashSet<uint>();
            private readonly List<(string signature, uint[] args)> calls = new List<(string, uint[])>();

            public ProgramReader(SynUdonUsageScan scan, AbstractUdonProgramSource source, IUdonProgram program, List<UdonBehaviour> behaviours, bool anySetProgramVariable)
            {
                this.scan = scan;
                this.source = source;
                this.program = program;
                this.behaviours = behaviours;
                this.anySetProgramVariable = anySetProgramVariable;
            }

            public void Read()
            {
                if (!Decode()) return;

                bool setsKeywords = false;
                bool keywordCollections = false;
                foreach (var (signature, _) in calls)
                {
                    if (IsKeywordSetter(signature)) setsKeywords = true;
                    if (IsKeywordCollection(signature)) keywordCollections = true;
                }

                if (setsKeywords)
                {
                    if (keywordCollections)
                    {
                        scan.MarkKeywordsUnknown($"'{source.name}' sets shader keywords from a list");
                    }
                    else
                    {
                        // Every string handed to a keyword call: the setters themselves and the LocalKeyword /
                        // GlobalKeyword constructors and FindKeyword calls that produce their arguments
                        foreach (var (signature, args) in calls)
                        {
                            if (signature.IndexOf("Keyword", StringComparison.Ordinal) < 0) continue;
                            if (!AddStringArguments(args, scan.Keywords))
                            {
                                scan.MarkKeywordsUnknown($"'{source.name}' builds a shader keyword name at runtime");
                                break;
                            }
                        }
                    }
                }

                foreach (var (signature, args) in calls)
                {
                    if (!signature.StartsWith("UnityEngineMaterial.", StringComparison.Ordinal)) continue;

                    if (signature.Contains("__get_mainTexture__"))
                    {
                        scan.TextureReads.Add("_MainTex");
                    }
                    else if (signature.Contains("__GetTexture__"))
                    {
                        // By name, or by an ID from PropertyToID: then any name the program converts could be it
                        bool resolved = signature.Contains("__GetTexture__SystemString__")
                            ? AddStringArguments(args, scan.TextureReads)
                            : AddPropertyToIdNames(scan.TextureReads);
                        if (!resolved) scan.MarkTextureReadsUnknown($"'{source.name}' reads a material texture by a name built at runtime");
                    }
                    else if (signature.Contains("__GetTexturePropertyNames") || signature.Contains("__GetTexturePropertyNameIDs") || signature.Contains("__CopyPropertiesFromMaterial__"))
                    {
                        scan.MarkTextureReadsUnknown($"'{source.name}' copies or lists material textures");
                    }
                }
            }

            private static bool IsKeywordSetter(string signature)
            {
                return signature.Contains("__EnableKeyword__") || signature.Contains("__DisableKeyword__") || signature.Contains("__SetKeyword__")
                    || signature.Contains("__set_shaderKeywords__") || signature.Contains("__set_enabledKeywords__");
            }

            // Keyword lists can't be traced to names (copying keywords between materials, setting a whole array)
            private static bool IsKeywordCollection(string signature)
            {
                return signature.Contains("__set_shaderKeywords__") || signature.Contains("__set_enabledKeywords__")
                    || signature.Contains("__get_shaderKeywords__") || signature.Contains("__get_enabledKeywords__")
                    || signature.Contains("__get_keywords__") || signature.Contains("__get_keywordNames__")
                    || signature.Contains("__get_globalKeywords__") || signature.Contains("__get_enabledGlobalKeywords__");
            }

            // Linear decode: Udon compilers push a call's arguments right before it, so the stack at an EXTERN holds them
            private bool Decode()
            {
                byte[] code = program.ByteCode;
                var stack = new List<uint>();
                for (int pc = 0; pc + 4 <= code.Length;)
                {
                    uint op = ReadWord(code, pc);
                    if (op > OpCopy || op == 3)
                    {
                        scan.MarkKeywordsUnknown($"'{source.name}' has bytecode this version does not understand");
                        scan.MarkTextureReadsUnknown($"'{source.name}' has bytecode this version does not understand");
                        return false;
                    }
                    uint operand = OperandCount(op) == 1 && pc + 8 <= code.Length ? ReadWord(code, pc + 4) : 0;

                    switch (op)
                    {
                        case OpPush:
                            stack.Add(operand);
                            break;
                        case OpPop:
                        case OpJumpIfFalse:
                            if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                            break;
                        case OpCopy:
                            if (stack.Count >= 2)
                            {
                                uint destination = stack[stack.Count - 1];
                                uint from = stack[stack.Count - 2];
                                if (!copySources.TryGetValue(destination, out var sources)) copySources[destination] = sources = new List<uint>();
                                sources.Add(from);
                                stack.RemoveRange(stack.Count - 2, 2);
                            }
                            break;
                        case OpExtern:
                            if (program.Heap.TryGetHeapVariable(operand, out object sig) && sig is string signature)
                            {
                                uint[] args = stack.ToArray();
                                calls.Add((signature, args));
                                MarkExternOutputs(signature, args);
                            }
                            stack.Clear();
                            break;
                    }
                    pc += OperandCount(op) == 1 ? 8 : 4;
                }
                return true;
            }

            // The return value is the last argument pushed; out/ref parameters can be any of them
            private void MarkExternOutputs(string signature, uint[] args)
            {
                if (args.Length == 0) return;
                if (signature.IndexOf("Ref", StringComparison.Ordinal) >= 0)
                {
                    foreach (uint arg in args) externOutputs.Add(arg);
                }
                else if (!signature.EndsWith("__SystemVoid", StringComparison.Ordinal))
                {
                    externOutputs.Add(args[args.Length - 1]);
                }
            }

            // Adds every value the string arguments of a call can have. False if one can't be traced.
            private bool AddStringArguments(uint[] args, HashSet<string> into)
            {
                foreach (uint arg in args)
                {
                    Type type = SafeHeapType(arg);
                    if (type != typeof(string)) continue;
                    if (!ResolveStrings(arg, into, new HashSet<uint>())) return false;
                }
                return true;
            }

            private bool AddPropertyToIdNames(HashSet<string> into)
            {
                bool any = false;
                foreach (var (signature, args) in calls)
                {
                    if (!signature.Contains("__PropertyToID__")) continue;
                    any = true;
                    if (!AddStringArguments(args, into)) return false;
                }
                return any;
            }

            private bool ResolveStrings(uint address, HashSet<string> into, HashSet<uint> visited)
            {
                if (!visited.Add(address)) return true;
                if (externOutputs.Contains(address)) return false;

                if (program.SymbolTable.TryGetSymbolFromAddress(address, out string symbol) && IsExternallyWritable(symbol))
                {
                    if (anySetProgramVariable) return false;
                    foreach (UdonBehaviour behaviour in behaviours)
                    {
                        if (behaviour.publicVariables != null && behaviour.publicVariables.TryGetVariableValue(symbol, out object value) && value is string publicValue)
                            into.Add(publicValue);
                    }
                }

                if (program.Heap.TryGetHeapVariable(address, out object initial) && initial is string text) into.Add(text);

                if (copySources.TryGetValue(address, out var sources))
                {
                    foreach (uint from in sources)
                    {
                        if (!ResolveStrings(from, into, visited)) return false;
                    }
                }
                return true;
            }

            // Public variables are set per behaviour in the inspector; synced ones by the network
            private bool IsExternallyWritable(string symbol)
            {
                if (program.SymbolTable.HasExportedSymbol(symbol)) return true;
                try
                {
                    return program.SyncMetadataTable?.GetSyncMetadataFromSymbol(symbol) != null;
                }
                catch
                {
                    return false;
                }
            }

            private Type SafeHeapType(uint address)
            {
                try
                {
                    return program.Heap.GetHeapVariableType(address);
                }
                catch
                {
                    return null;
                }
            }
        }
    }
}
