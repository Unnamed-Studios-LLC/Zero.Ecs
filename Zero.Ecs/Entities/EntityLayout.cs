using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Zero.Ecs
{
    public unsafe sealed class EntityLayout
    {
        private abstract class EntityLayoutComponent
        {
            public abstract int Type { get; }

            public abstract void Set(byte* list, int listIndex);
        }

        private class EntityLayoutComponent<T> : EntityLayoutComponent where T : unmanaged
        {
            public T Default { get; set; }
            public override int Type => TypeCache<T>.Type;

            public override void Set(byte* list, int listIndex)
            {
                *((T*)list + listIndex) = Default;
            }
        }

        private readonly SortedList<int, EntityLayoutComponent> _components = new SortedList<int, EntityLayoutComponent>();

        internal EntityArchetype AddArchetype { get; private set; } = new EntityArchetype(Array.Empty<ulong>());
        internal EntityArchetype RemoveArchetype { get; private set; } = new EntityArchetype(Array.Empty<ulong>());

        public unsafe EntityLayout DefineAdd<T>(T @default = default) where T : unmanaged
        {
            var type = TypeCache<T>.Type;
            if (_components.TryGetValue(type, out var component))
            {
                ((EntityLayoutComponent<T>)component).Default = @default;
                return this;
            }

            var typedComponent = new EntityLayoutComponent<T>()
            {
                Default = @default
            };

            var typeDepth = type / 64;
            if (typeDepth >= AddArchetype.DepthCount)
            {
                var newArchetypes = new ulong[typeDepth + 1];
                fixed (ulong* newArchPntr = newArchetypes)
                fixed (ulong* curArchPntr = AddArchetype.Archetypes)
                {
                    for (int i = 0; i < AddArchetype.DepthCount; i++)
                    {
                        *(newArchPntr + i) = *(curArchPntr + i);
                    }
                }
                AddArchetype = new EntityArchetype(newArchetypes);
            }
            AddArchetype.Archetypes[typeDepth] |= 1ul << (type % 64);

            // remove type from remove archetype
            if (type < RemoveArchetype.DepthCount)
            {
                RemoveArchetype.Archetypes[typeDepth] &= ~(1ul << (type % 64));
            }
            
            _components.Add(type, typedComponent);

            return this;
        }

        public unsafe EntityLayout DefineRemove<T>() where T : unmanaged
        {
            var type = TypeCache<T>.Type;
            var typeDepth = type / 64;
            if (typeDepth >= RemoveArchetype.DepthCount)
            {
                var newArchetypes = new ulong[typeDepth + 1];
                fixed (ulong* newArchPntr = newArchetypes)
                fixed (ulong* curArchPntr = RemoveArchetype.Archetypes)
                {
                    for (int i = 0; i < RemoveArchetype.DepthCount; i++)
                    {
                        *(newArchPntr + i) = *(curArchPntr + i);
                    }
                }
                RemoveArchetype = new EntityArchetype(newArchetypes);
            }
            RemoveArchetype.Archetypes[typeDepth] |= 1ul << (type % 64);

            // remove type from add archetype
            if (type < AddArchetype.DepthCount)
            {
                AddArchetype.Archetypes[typeDepth] &= ~(1ul << (type % 64));
                _components.Remove(type);
            }

            return this;
        }

        internal void Set(uint entityId, ref EntityReference reference, EntityArchetype previousArchetype, Entities entities)
        {
            int j = 0;
            EntityLayoutComponent component = null;
            byte* chunk = (byte*)reference.Group.Chunks[reference.ChunkIndex].ToPointer();
            for (int i = 0; i < reference.Group.NonZeroComponentListCount; i++)
            {
                var sourceType = reference.Group.NonZeroComponentTypes[i];
                var list = chunk + reference.Group.ComponentListOffsets[i];
                if (component == null || component.Type < sourceType)
                {
                    do
                    {
                        if (j >= _components.Count)
                        {
                            return;
                        }
                        component = _components.Values[j++];
                    }
                    while (component.Type < sourceType);
                }

                if (component.Type == sourceType)
                {
                    var isAdded = previousArchetype.Archetypes != null && !previousArchetype.Contains(sourceType);
                    component.Set(list, reference.ListIndex);
                }
            }
        }
    }
}
