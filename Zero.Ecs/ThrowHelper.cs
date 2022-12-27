using System;
using System.Runtime.CompilerServices;

namespace Zero.Ecs
{
    internal class ThrowHelper
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ThrowInvalidEntityId()
        {
            throw new Exception("Entity does not exist at the given id");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void ThrowWorldInParallel()
        {
            throw new Exception("Unable to enter ParallelForEach, the executing world is marked as Parallel update");
        }
    }
}
