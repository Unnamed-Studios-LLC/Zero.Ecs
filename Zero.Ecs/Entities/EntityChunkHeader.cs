using System.Runtime.InteropServices;

namespace Zero.Ecs
{
    [StructLayout(LayoutKind.Explicit, Size = 4)]
    internal unsafe struct EntityChunkHeader
    {
        [FieldOffset(0)]
        public int Count;
    }
}