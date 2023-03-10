using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Zero.Ecs
{
    public unsafe partial class Entities
    {
        internal static bool AllowStructuralChangeGlobal = true;
        internal bool RunningInParallel = false;

        private readonly List<EntityGroup> _groups = new List<EntityGroup>();
        private readonly CompositeKeyDictionary<ulong, EntityGroup> _groupLocator = new CompositeKeyDictionary<ulong, EntityGroup>();
        private readonly Dictionary<uint, EntityReference> _entityLocationMap = new Dictionary<uint, EntityReference>();
        private readonly List<IntPtr> _componentListIndicesCache = new List<IntPtr>();
        private readonly List<(EntityGroup, IntPtr, int)> _parallelChunkList = new List<(EntityGroup, IntPtr, int)>();
        private int _usedListIndices = 0;
        private uint _nextEntityId = 1;
        private bool _iterating = false;

        internal void Dispose()
        {
            for (int i = 0; i < _groups.Count; i++)
            {
                _groups[i].Dispose();
            }
            _groups.Clear();

            foreach (var listIndices in _componentListIndicesCache)
            {
                Marshal.FreeHGlobal(listIndices);
            }
            _componentListIndicesCache.Clear();
        }

        private static void CopyOverlapingComponents(ref EntityReference source, ref EntityReference destination)
        {
            int j = -1;
            int destinationType = -1;
            for (int i = 0; i < source.Group.NonZeroComponentListCount; i++)
            {
                var sourceType = source.Group.NonZeroComponentTypes[i];

                while (destinationType < sourceType)
                {
                    if (++j >= destination.Group.NonZeroComponentListCount)
                    {
                        return;
                    }
                    destinationType = destination.Group.NonZeroComponentTypes[j];
                }

                if (destinationType == sourceType)
                {
                    var srcPtr = source.Group.GetComponent(source.ChunkIndex, i, source.ListIndex, out var size);
                    var dstPtr = destination.Group.GetComponent(destination.ChunkIndex, j, destination.ListIndex, out size);
                    Buffer.MemoryCopy(srcPtr, dstPtr, size, size);
                }
            }
        }

        private unsafe void EnsureGroup(ulong* archetypes, int depth, out EntityGroup group)
        {
            if (_groupLocator.TryGetValue(archetypes, depth, out group))
            {
                return;
            }

            // construct archetype
            var archetypesArray = new ulong[depth];
            fixed (ulong* arrayPntr = archetypesArray)
            {
                for (int i = 0; i < depth; i++)
                {
                    *(arrayPntr + i) = *(archetypes + i);
                }
            }

            group = new EntityGroup(new EntityArchetype(archetypesArray));
            _groupLocator.Insert(archetypes, depth, group);
            _groups.Add(group);
        }

        private unsafe void ForEach<T>(T query) where T : IQuery
        {
            _iterating = true;

            try
            {
                int* indices = stackalloc int[6];
                query.AddRequiredArchetypes(ref _with, ref _withDepth);

                for (int i = 0; i < _groups.Count; i++)
                {
                    var group = _groups[i];
                    if (!GroupInQuery(group))
                    {
                        continue;
                    }

                    query.GetComponentListIndex(group, indices);

                    for (int j = 0; j < group.Chunks.Count; j++)
                    {
                        if (group.GetChunkCount(j) == 0)
                        {
                            continue;
                        }

                        query.Func(group, j, indices);
                    }
                }
            }
            finally
            {
                ZeroFilters();
                _iterating = false;
            }
        }

        private uint GenerateEntityId()
        {
            uint id;
            do
            {
                id = _nextEntityId++;
            }
            while (_entityLocationMap.ContainsKey(id));
            return id;
        }

        private int* GetListIndices()
        {
            IntPtr ptr;
            if (_usedListIndices < _componentListIndicesCache.Count)
            {
                ptr = _componentListIndicesCache[_usedListIndices++];
                return (int*)ptr.ToPointer();
            }

            _usedListIndices++;
            ptr = Marshal.AllocHGlobal(sizeof(int) * 6);
            _componentListIndicesCache.Add(ptr);
            return (int*)ptr.ToPointer();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool GroupInQuery(EntityGroup group)
        {
            return group.Archetype.ContainsAny(_any, _anyDepth + 1) &&
                !group.Archetype.ContainsAny(_no, _noDepth + 1) &&
                group.Archetype.ContainsAll(_with, _withDepth + 1);
        }

        private unsafe void ParallelForEach<T>(T query) where T : IQuery
        {
            if (RunningInParallel)
            {
                ForEach(query);
                return;
            }

            _iterating = true;
            try
            {
                _parallelChunkList.Clear();
                query.AddRequiredArchetypes(ref _with, ref _withDepth);

                for (int i = 0; i < _groups.Count; i++)
                {
                    var group = _groups[i];
                    if (!GroupInQuery(group))
                    {
                        continue;
                    }

                    var indices = GetListIndices();
                    query.GetComponentListIndex(group, indices);

                    for (int j = 0; j < group.Chunks.Count; j++)
                    {
                        if (group.GetChunkCount(j) == 0)
                        {
                            continue;
                        }

                        _parallelChunkList.Add((group, new IntPtr(indices), j));
                    }
                }

                Parallel.ForEach(_parallelChunkList, (pair) =>
                {
                    var (group, indicesPtr, chunkIndex) = pair;
                    var indices = (int*)indicesPtr.ToPointer();
                    query.Func(group, chunkIndex, indices);
                });

                ReturnListIndices();
            }
            finally
            {
                ZeroFilters();
                _iterating = false;
            }
        }

        private unsafe void RemoveComponents(uint entityId, ulong* removeArchetype, int removeDepth)
        {
            ThrowIfIterating();
            if (!_entityLocationMap.TryGetValue(entityId, out var reference)) // no entity found
            {
                ThrowHelper.ThrowInvalidEntityId();
            }

            var newArchetype = false;
            if (reference.Group == null ||
                !reference.Group.Archetype.ContainsAny(removeArchetype, removeDepth)) // no group or type of component found
            {
                return;
            }

            newArchetype = !reference.Group.Archetype.ContainsAll(removeArchetype, removeDepth);

            EntityReference newReference;
            if (newArchetype)
            {
                // get new archetype bit field

                // decrement depth if removing whole types in higher indices
                var currentDepth = reference.Group.Archetype.DepthCount;
                if (removeDepth >= currentDepth)
                {
                    while ((reference.Group.Archetype.Archetypes[currentDepth - 1] & ~removeArchetype[currentDepth - 1]) == 0 ||
                        reference.Group.Archetype.Archetypes[currentDepth - 1] == 0)
                    {
                        currentDepth--;
                    }
                }

                // create new archetypes
                ulong* archetypes = stackalloc ulong[currentDepth];
                fixed (ulong* archPntr = reference.Group.Archetype.Archetypes)
                {
                    for (int i = 0; i < currentDepth; i++)
                    {
                        if (i < removeDepth) *(archetypes + i) = (*(archPntr + i)) & (~*(removeArchetype + i));
                        else *(archetypes + i) = *(archPntr + i);
                    }
                }

                EnsureGroup(archetypes, currentDepth, out var newGroup);
                newReference = newGroup.GetNextSlot(entityId);

                CopyOverlapingComponents(ref reference, ref newReference);
            }
            else
            {
                newReference = new EntityReference(null, 0, 0);
            }

            _entityLocationMap[entityId] = newReference;

            // now patch and remap entity (we are done with old entity data)
            var remappedEntity = reference.Group.Remove(reference.ChunkIndex, reference.ListIndex);
            if (remappedEntity != 0)
            {
                _entityLocationMap[remappedEntity] = reference;
            }
        }

        private void ReturnListIndices()
        {
            _usedListIndices = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ThrowIfIterating()
        {
            if (!_iterating && AllowStructuralChangeGlobal)
            {
                return;
            }
            throw new Exception("Entity structural change is not allowed while iterating. Use a command buffer to execute changes after the iteration");
        }

        private void ZeroFilters()
        {
            _anyDepth = -1;
            _noDepth = 0;
            _withDepth = -1;
            _no.Archetypes[0] = TypeCache.DisabledArchetypeMask;
        }
    }
}
