using System;
using System.Collections.Generic;

namespace Zero.Ecs
{
    internal delegate void AddDelegate(Entities entities, uint entityId, ref EntityReference reference);
    internal delegate void RemoveDelegate(Entities entities, uint entityId, in EntityReference reference);

    internal unsafe static class TypeCache
    {
        public static object Lock = new object();
        public static int NextType = 1;
        public const int MaxComponentTypes = ushort.MaxValue;
        public static int DisabledType = 0;
        public static ulong DisabledArchetypeMask = 1;

        public static List<int> Sizes = new List<int>() { sizeof(Disabled) };
        public static List<bool> ZeroSize = new List<bool>() { true };
        public static Dictionary<Type, int> Types = new Dictionary<Type, int>() { [typeof(Disabled)] = DisabledType };

        static TypeCache()
        {

        }
    }

    internal static unsafe class TypeCache<T> where T : unmanaged
    {
        private static int? _index;

        public static T NullRef = default;
        public static int Type = Generate();
        public static bool ZeroSize;

        private static int Generate()
        {
            lock (TypeCache.Lock)
            {
                if (_index != null)
                {
                    return _index.Value;
                }

                var type = typeof(T);
                if (type.Equals(typeof(Disabled)))
                {
                    _index = TypeCache.DisabledType;
                    ZeroSize = type.IsZeroSize();
                    return _index.Value;
                }

                if (TypeCache.NextType == TypeCache.MaxComponentTypes)
                {
                    throw new Exception("Maximum types reached");
                }

                if (sizeof(T) > ushort.MaxValue)
                {
                    throw new Exception($"Component {typeof(T).FullName} exceeds the max component size of {ushort.MaxValue}");
                }

                _index = TypeCache.NextType++;
                TypeCache.ZeroSize.Add(ZeroSize);
                TypeCache.Types.Add(type, _index.Value);
                TypeCache.Sizes.Add(sizeof(T));
                ZeroSize = type.IsZeroSize();
                return _index.Value;
            }
        }
    }
}
