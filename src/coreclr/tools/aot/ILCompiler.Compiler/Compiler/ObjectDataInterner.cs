﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;

using ILCompiler.DependencyAnalysis;

using Debug = System.Diagnostics.Debug;

namespace ILCompiler
{
    public sealed class ObjectDataInterner
    {
        private Dictionary<ISymbolNode, ISymbolNode> _symbolRemapping;

        public static ObjectDataInterner Null { get; } = new ObjectDataInterner() { _symbolRemapping = new() };

        public bool IsNull => _symbolRemapping != null && _symbolRemapping.Count == 0;

        private void EnsureMap(NodeFactory factory)
        {
            Debug.Assert(factory.MarkingComplete);

            if (_symbolRemapping != null)
                return;

            HashSet<MethodInternKey> previousMethodHash;
            HashSet<MethodInternKey> methodHash = null;
            Dictionary<ISymbolNode, ISymbolNode> previousSymbolRemapping;
            Dictionary<ISymbolNode, ISymbolNode> symbolRemapping = null;

            do
            {
                previousMethodHash = methodHash;
                previousSymbolRemapping = symbolRemapping;
                methodHash = new HashSet<MethodInternKey>(previousMethodHash?.Count ?? 0, new MethodInternComparer(factory, previousSymbolRemapping));
                symbolRemapping = new Dictionary<ISymbolNode, ISymbolNode>((int)(1.05 * (previousSymbolRemapping?.Count ?? 0)));

                foreach (IMethodBodyNode body in factory.MetadataManager.GetCompiledMethodBodies())
                {
                    // We don't track special unboxing thunks as virtual method use related so ignore them
                    if (body is ISpecialUnboxThunkNode unboxThunk && unboxThunk.IsSpecialUnboxingThunk)
                        continue;

                    // Bodies that are visible from outside should not be folded because we don't know
                    // if they're address taken.
                    if (factory.GetSymbolAlternateName(body, out _) != null)
                        continue;

                    var key = new MethodInternKey(body, factory);
                    if (methodHash.TryGetValue(key, out MethodInternKey found))
                    {
                        symbolRemapping.Add(body, found.Method);
                    }
                    else
                    {
                        methodHash.Add(key);
                    }
                }
            } while (previousSymbolRemapping == null || previousSymbolRemapping.Count < symbolRemapping.Count);

            _symbolRemapping = symbolRemapping;
        }

        public ISymbolNode GetDeduplicatedSymbol(NodeFactory factory, ISymbolNode original)
        {
            EnsureMap(factory);

            ISymbolNode target = original;
            if (target is ISymbolNodeWithLinkage symbolWithLinkage)
                target = symbolWithLinkage.NodeForLinkage(factory);

            return _symbolRemapping.TryGetValue(target, out ISymbolNode result) ? result : original;
        }

        private sealed class MethodInternKey
        {
            public IMethodBodyNode Method { get; }
            public int HashCode { get; }

            public MethodInternKey(IMethodBodyNode node, NodeFactory factory)
            {
                ObjectNode.ObjectData data = ((ObjectNode)node).GetData(factory, relocsOnly: false);

                var hashCode = default(HashCode);
                hashCode.AddBytes(data.Data);

                var nodeWithCodeInfo = (INodeWithCodeInfo)node;

                hashCode.AddBytes(nodeWithCodeInfo.GCInfo);

                foreach (FrameInfo fi in nodeWithCodeInfo.FrameInfos)
                    hashCode.Add(fi.GetHashCode());

                ObjectNode.ObjectData ehData = nodeWithCodeInfo.EHInfo?.GetData(factory, relocsOnly: false);

                if (ehData is not null)
                    hashCode.AddBytes(ehData.Data);

                HashCode = hashCode.ToHashCode();
                Method = node;
            }
        }

        private sealed class MethodInternComparer : IEqualityComparer<MethodInternKey>
        {
            private readonly NodeFactory _factory;
            private readonly Dictionary<ISymbolNode, ISymbolNode> _interner;

            public MethodInternComparer(NodeFactory factory, Dictionary<ISymbolNode, ISymbolNode> interner)
                => (_factory, _interner) = (factory, interner);

            public int GetHashCode(MethodInternKey key) => key.HashCode;

            private static bool AreSame(ReadOnlySpan<byte> o1, ReadOnlySpan<byte> o2) => o1.SequenceEqual(o2);

            private bool AreSame(ObjectNode.ObjectData o1, ObjectNode.ObjectData o2)
            {
                if (AreSame(o1.Data, o2.Data) && o1.Relocs.Length == o2.Relocs.Length)
                {
                    for (int i = 0; i < o1.Relocs.Length; i++)
                    {
                        ref Relocation r1 = ref o1.Relocs[i];
                        ref Relocation r2 = ref o2.Relocs[i];
                        if (r1.RelocType != r2.RelocType
                            || r1.Offset != r2.Offset)
                        {
                            return false;
                        }

                        if (r1.Target != r2.Target)
                        {
                            if (r1.Target is MethodReadOnlyDataNode rodata1
                                && r2.Target is MethodReadOnlyDataNode rodata2
                                && AreSame(rodata1.GetData(_factory, relocsOnly: false), rodata2.GetData(_factory, relocsOnly: false)))
                            {
                                // We can consider same MethodReadOnlyDataNode the same.
                            }
                            else if (_interner != null &&
                                ((_interner.TryGetValue(r1.Target, out ISymbolNode t1) && r2.Target == t1)
                                || (_interner.TryGetValue(r2.Target, out ISymbolNode t2) && r1.Target == t2)))
                            {
                                // These got already interned
                            }
                            else
                            {
                                return false;
                            }
                        }
                    }

                    return true;
                }

                return false;
            }

            public bool Equals(MethodInternKey a, MethodInternKey b)
            {
                if (a.HashCode != b.HashCode)
                    return false;

                ObjectNode.ObjectData o1data = ((ObjectNode)a.Method).GetData(_factory, relocsOnly: false);
                ObjectNode.ObjectData o2data = ((ObjectNode)b.Method).GetData(_factory, relocsOnly: false);

                if (!AreSame(o1data, o2data))
                    return false;

                var o1codeinfo = (INodeWithCodeInfo)a.Method;
                var o2codeinfo = (INodeWithCodeInfo)b.Method;
                if (!AreSame(o1codeinfo.GCInfo, o2codeinfo.GCInfo))
                    return false;

                FrameInfo[] o1frames = o1codeinfo.FrameInfos;
                FrameInfo[] o2frames = o2codeinfo.FrameInfos;
                if (o1frames.Length != o2frames.Length)
                    return false;

                for (int i = 0; i < o1frames.Length; i++)
                {
                    if (!o1frames[i].Equals(o2frames[i]))
                        return false;
                }

                MethodExceptionHandlingInfoNode o1eh = o1codeinfo.EHInfo;
                MethodExceptionHandlingInfoNode o2eh = o2codeinfo.EHInfo;

                if (o1eh == o2eh)
                    return true;

                if (o1eh == null || o2eh == null)
                    return false;

                return AreSame(o1eh.GetData(_factory, relocsOnly: false), o2eh.GetData(_factory, relocsOnly: false));
            }
        }
    }
}
