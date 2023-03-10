using System;

namespace Zero.Ecs
{
    public sealed partial class Entities
    {
        /// <summary>
        /// The count of entities contained
        /// </summary>
        public int EntityCount { get; internal set; }

        /// <summary>
        /// Adds a new component to a given entity. If the component already exists, the values are overriden.
        /// This operation invokes a structural change and cannot be called within queries or from threads other than the main thread.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="entityId"></param>
        /// <param name="component"></param>
        /// <returns></returns>
        public unsafe ref T AddComponent<T>(uint entityId, T component = default) where T : unmanaged
        {
            ThrowIfIterating();
            if (!_entityLocationMap.TryGetValue(entityId, out var reference)) // no entity found
            {
                ThrowHelper.ThrowInvalidEntityId();
            }

            var type = TypeCache<T>.Type;
            var copyOld = false;
            var archetypeChange = false;
            if (reference.Group == null) // no group
            {
                // archetype change
                archetypeChange = true;
            }
            else if(!reference.Group.Archetype.Contains(type))
            {
                // archetype change
                archetypeChange = true;
                copyOld = true;
            }

            if (archetypeChange)
            {
                // get new archetype bit field
                var typeDepth = type / 64;
                var currentDepth = reference.Group == null ? 0 : reference.Group.Archetype.DepthCount;
                var maxDepth = typeDepth >= currentDepth ? typeDepth + 1 : currentDepth;
                ulong* archetypes = stackalloc ulong[maxDepth];
                *(archetypes + typeDepth) = 1u << (type % 64);
                if (reference.Group != null)
                {
                    fixed (ulong* archPntr = reference.Group.Archetype.Archetypes)
                    {
                        for (int i = 0; i < currentDepth; i++)
                        {
                            *(archetypes + i) |= *(archPntr + i);
                        }
                    }
                }

                EnsureGroup(archetypes, maxDepth, out var newGroup);
                var newReference = newGroup.GetNextSlot(entityId);

                if (copyOld)
                {
                    CopyOverlapingComponents(ref reference, ref newReference);
                    var remappedEntity = reference.Group.Remove(reference.ChunkIndex, reference.ListIndex);
                    if (remappedEntity != 0)
                    {
                        _entityLocationMap[remappedEntity] = reference;
                    }
                }

                _entityLocationMap[entityId] = newReference;
                reference = newReference;
            }

            ref var componentRef = ref reference.GetComponent<T>(type);
            if (!TypeCache<T>.ZeroSize)
            {
                componentRef = component;
            }

            return ref componentRef;
        }

        /// <summary>
        /// Applies a given entity layout to a new entity. Existing components are not removed.
        /// Components in the layout that already exist on the entity are overriden with the layout values.
        /// This operation invokes a structural change and cannot be called within queries or from threads other than the main thread.
        /// </summary>
        /// <param name="entityId"></param>
        /// <param name="layout"></param>
        public unsafe void ApplyLayout(uint entityId, EntityLayout layout)
        {
            if (layout is null)
            {
                throw new ArgumentNullException(nameof(layout));
            }

            ThrowIfIterating();
            if (layout.AddArchetype.Archetypes.Length == 0 &&
                layout.RemoveArchetype.Archetypes.Length == 0)
            {
                return;
            }

            if (!_entityLocationMap.TryGetValue(entityId, out var reference))
            {
                ThrowHelper.ThrowInvalidEntityId();
            }

            var copyOld = false;
            var archetypeChange = false;
            EntityArchetype previousArchetype = reference.Group?.Archetype ?? default;
            if (reference.Group == null) // no group
            {
                // archetype change
                archetypeChange = true;
            }
            else
            {
                if (!reference.Group.Archetype.ContainsAll(layout.AddArchetype) ||
                    reference.Group.Archetype.ContainsAny(layout.RemoveArchetype))
                {
                    // archetype change
                    archetypeChange = true;
                    copyOld = true;
                }
            }
            
            if (archetypeChange)
            {
                // get new archetype bit field
                var layoutAddDepth = layout.AddArchetype.DepthCount;
                var layoutRemoveDepth = layout.RemoveArchetype.DepthCount;
                var currentDepth = reference.Group == null ? 0 : reference.Group.Archetype.DepthCount;

                // decrement depth if removing whole types in higher indices
                if (reference.Group != null && layoutRemoveDepth >= currentDepth)
                {
                    while ((reference.Group.Archetype.Archetypes[currentDepth - 1] & ~layout.RemoveArchetype.Archetypes[currentDepth - 1]) == 0 ||
                        reference.Group.Archetype.Archetypes[currentDepth - 1] == 0)
                    {
                        currentDepth--;
                    }
                }

                var maxDepth = layoutAddDepth > currentDepth ? layoutAddDepth : currentDepth;
                ulong* archetypes = stackalloc ulong[maxDepth];

                // apply layout add archetypes
                fixed (ulong* archPntr = layout.AddArchetype.Archetypes)
                {
                    for (int i = 0; i < layoutAddDepth; i++)
                    {
                        *(archetypes + i) = *(archPntr + i);
                    }
                }

                if (reference.Group != null)
                {
                    fixed (ulong* archPntr = reference.Group.Archetype.Archetypes)
                    fixed (ulong* removeArchPntr = layout.RemoveArchetype.Archetypes)
                    {
                        for (int i = 0; i < currentDepth; i++)
                        {
                            if (i < layoutRemoveDepth) *(archetypes + i) |= (*(archPntr + i)) & (~*(removeArchPntr + i)); // apply with remove mask
                            else *(archetypes + i) |= *(archPntr + i); // apply
                        }
                    }
                }

                EnsureGroup(archetypes, maxDepth, out var newGroup);
                var newReference = newGroup.GetNextSlot(entityId);

                if (copyOld)
                {
                    CopyOverlapingComponents(ref reference, ref newReference);

                    _entityLocationMap[entityId] = newReference;
                    var remappedEntity = reference.Group.Remove(reference.ChunkIndex, reference.ListIndex);
                    if (remappedEntity != 0)
                    {
                        _entityLocationMap[remappedEntity] = reference;
                    }
                }
                else
                {
                    _entityLocationMap[entityId] = newReference;
                }

                reference = newReference;
            }

            layout.Set(entityId, ref reference, previousArchetype, this);
        }

        /// <summary>
        /// Creates a new empty entity
        /// This operation invokes a structural change and cannot be called within queries or from threads other than the main thread.
        /// </summary>
        /// <returns></returns>
        public uint CreateEntity()
        {
            ThrowIfIterating();
            var id = GenerateEntityId();
            var reference = new EntityReference(null, 0, 0);
            _entityLocationMap.Add(id, reference);
            EntityCount++;
            return id;
        }

        /// <summary>
        /// Creates a new entity with components defined in the given entity layout
        /// This operation invokes a structural change and cannot be called within queries or from threads other than the main thread.
        /// </summary>
        /// <param name="layout"></param>
        /// <returns></returns>
        public uint CreateEntity(EntityLayout layout)
        {
            if (layout is null)
            {
                throw new ArgumentNullException(nameof(layout));
            }

            var entityId = CreateEntity();
            ApplyLayout(entityId, layout);
            return entityId;
        }

        /// <summary>
        /// Creates a new entity based off of a given entity. The source and new entity will contain equal components
        /// This operation invokes a structural change and cannot be called within queries or from threads other than the main thread.
        /// </summary>
        /// <param name="sourceEntityId"></param>
        /// <returns></returns>
        public unsafe uint Clone(uint sourceEntityId)
        {
            ThrowIfIterating();
            if (!_entityLocationMap.TryGetValue(sourceEntityId, out var reference))
            {
                ThrowHelper.ThrowInvalidEntityId();
            }

            var entityId = CreateEntity();
            EntityReference newReference;
            if (reference.Group == null)
            {
                _entityLocationMap[entityId] = newReference = reference;
            }
            else
            {
                newReference = reference.Group.GetNextSlot(entityId);
                CopyOverlapingComponents(ref reference, ref newReference);
                _entityLocationMap[entityId] = newReference;
            }

            var typeCount = reference.Group.Archetype.TypeCount;
            int* types = stackalloc int[typeCount];
            reference.Group.Archetype.GetComponentTypes(types, typeCount);

            return entityId;
        }

        /// <summary>
        /// Destroys all entities
        /// This operation invokes a structural change and cannot be called within queries or from threads other than the main thread.
        /// </summary>
        public void DestroyAllEntities()
        {
            ThrowIfIterating();

            for (int i = 0; i < _groups.Count; i++)
            {
                var group = _groups[i];
                while (true)
                {
                    var entityId = group.GetLastEntityId(); // destroy entities backwards to avoid component patching
                    if (entityId == 0) break;
                    DestroyEntity(entityId);
                }
            }
            EntityCount = 0;
        }

        /// <summary>
        /// Destroy an entity given the entity's id
        /// This operation invokes a structural change and cannot be called within queries or from threads other than the main thread.
        /// </summary>
        /// <param name="entityId"></param>
        public unsafe void DestroyEntity(uint entityId)
        {
            ThrowIfIterating();
            if (!_entityLocationMap.TryGetValue(entityId, out var reference))
            {
                ThrowHelper.ThrowInvalidEntityId();
            }

            _entityLocationMap.Remove(entityId);

            if (reference.Group != null)
            {
                var remappedEntity = reference.Group.Remove(reference.ChunkIndex, reference.ListIndex);
                if (remappedEntity != 0)
                {
                    _entityLocationMap[remappedEntity] = reference;
                }
            }
            EntityCount--;
        }

        /// <summary>
        /// Returns if an entity exists with the given entity id
        /// </summary>
        /// <param name="entityId"></param>
        /// <returns></returns>
        public bool EntityExists(uint entityId)
        {
            return _entityLocationMap.ContainsKey(entityId);
        }

        /// <summary>
        /// Returns a reference to component T found on the given entity id. This method will throw if the entity or component doesn't exist.
        /// Consider using TryGetComponent you are unsure about the component's existence
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="entityId"></param>
        /// <returns></returns>
        public unsafe ref T GetComponent<T>(uint entityId) where T : unmanaged
        {
            var type = TypeCache<T>.Type;
            if (!_entityLocationMap.TryGetValue(entityId, out var reference)) // no entity found
            {
                ThrowHelper.ThrowInvalidEntityId();
            }

            if (reference.Group == null)
            {
                throw new Exception($"Component type {typeof(T).FullName} not found. Use {nameof(TryGetComponent)} to avoid exceptions being thrown.");
            }

            if (!reference.Group.Archetype.Contains(type)) // entity does not contain type
            {
                throw new Exception($"Component type {typeof(T).FullName} not found. Use {nameof(TryGetComponent)} to avoid exceptions being thrown.");
            }

            return ref reference.GetComponent<T>(type);
        }

        /// <summary>
        /// Returns a reference struct to an entities components.
        /// Used for multiple component gets and checks.
        /// This method will throw if the entity doesn't exist.
        /// The returned Components are invalid after any structural change
        /// </summary>
        /// <param name="entityId"></param>
        /// <returns></returns>
        public unsafe EntityComponents GetComponents(uint entityId)
        {
            if (!_entityLocationMap.TryGetValue(entityId, out var reference)) // no entity found
            {
                ThrowHelper.ThrowInvalidEntityId();
            }

            if (reference.Group == null)
            {
                return new EntityComponents(reference.Group, null, -1);
            }

            return new EntityComponents(reference.Group, reference.Group.GetChunk(reference.ChunkIndex), reference.ListIndex);
        }

        /// <summary>
        /// Returns if a given entity id contains a component of type T
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="entityId"></param>
        /// <returns></returns>
        public bool HasComponent<T>(uint entityId) where T : unmanaged
        {
            if (!_entityLocationMap.TryGetValue(entityId, out var reference)) // no entity found
            {
                ThrowHelper.ThrowInvalidEntityId();
            }

            if (reference.Group == null)
            {
                return false;
            }

            var type = TypeCache<T>.Type;
            return reference.Group.Archetype.Contains(type);
        }

        /// <summary>
        /// Returns if a given entity is marked as disabled
        /// </summary>
        /// <param name="entityId"></param>
        /// <returns></returns>
        public bool IsDisabled(uint entityId)
        {
            if (!_entityLocationMap.TryGetValue(entityId, out var reference)) // no entity found
            {
                ThrowHelper.ThrowInvalidEntityId();
            }

            if (reference.Group == null)
            {
                return false;
            }

            return reference.Group.Archetype.IsDisabledArchetype;
        }

        /// <summary>
        /// Removes a component T from a given entity. If the component doesn't exist on the entity, no operation is executed.
        /// An exception is thrown if the given entity does not exist.
        /// This operation invokes a structural change and cannot be called within queries or from threads other than the main thread.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="entityId"></param>
        public unsafe void RemoveComponent<T>(uint entityId) where T : unmanaged
        {
            ThrowIfIterating();
            if (!_entityLocationMap.TryGetValue(entityId, out var reference)) // no entity found
            {
                ThrowHelper.ThrowInvalidEntityId();
            }

            var type = TypeCache<T>.Type;
            var newArchetype = false;
            if (reference.Group == null ||
                !reference.Group.Archetype.Contains(type)) // no group or type of component found
            {
                return;
            }

            newArchetype = reference.Group.Archetype.NonZeroTypeCount != 1;

            EntityReference newReference;
            T removedValue = reference.GetComponent<T>(TypeCache<T>.Type);
            if (newArchetype)
            {
                // get new archetype bit field
                var typeDepth = type / 64;
                var currentDepth = reference.Group.Archetype.DepthCount;
                if (typeDepth + 1 == currentDepth && reference.Group.Archetype.Archetypes[typeDepth] == (1ul << (type % 64)))
                {
                    do
                    {
                        currentDepth--;
                    }
                    while (reference.Group.Archetype.Archetypes[currentDepth - 1] == 0);
                }

                ulong* archetypes = stackalloc ulong[currentDepth];
                fixed (ulong* archPntr = reference.Group.Archetype.Archetypes)
                {
                    for (int i = 0; i < currentDepth; i++)
                    {
                        *(archetypes + i) = *(archPntr + i);
                    }

                    if (typeDepth <= currentDepth)
                    {
                        *(archetypes + typeDepth) &= ~(1ul << (type % 64));
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

            var remappedEntity = reference.Group.Remove(reference.ChunkIndex, reference.ListIndex);
            if (remappedEntity != 0)
            {
                _entityLocationMap[remappedEntity] = reference;
            }

            _entityLocationMap[entityId] = newReference;
        }

        /// <summary>
        /// Sets a entity's disabled value. By default, disabled entities are not included in queries.
        /// This operation invokes a structural change and cannot be called within queries or from threads other than the main thread.
        /// </summary>
        /// <param name="entityId"></param>
        /// <param name="disabled"></param>
        public void SetDisabled(uint entityId, bool disabled)
        {
            if (disabled)
            {
                AddComponent<Disabled>(entityId);
            }
            else
            {
                RemoveComponent<Disabled>(entityId);
            }
        }

        /// <summary>
        /// Returns a reference to component T found on the given entity id. Found is assigned to if the component is found on the entity.
        /// A valid entityId is still required.
        /// If found is false, the returned ref is a throwaway ref.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="entityId"></param>
        /// <param name="found"></param>
        /// <returns></returns>
        public unsafe ref T TryGetComponent<T>(uint entityId, out bool found) where T : unmanaged
        {
            if (!_entityLocationMap.TryGetValue(entityId, out var reference)) // no entity found
            {
                ThrowHelper.ThrowInvalidEntityId();
            }

            if (reference.Group == null) // entity has no components
            {
                found = false;
                return ref TypeCache<T>.NullRef;
            }

            var type = TypeCache<T>.Type;
            if (!reference.Group.Archetype.Contains(type)) // entity does not contain type
            {
                found = false;
                return ref TypeCache<T>.NullRef;
            }

            found = true;
            return ref reference.GetComponent<T>(type);
        }
    }
}
