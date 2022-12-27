﻿using System;
using System.Linq;
using System.Reflection;

namespace Zero.Ecs
{
    internal static class TypeExtensions
    {
        public static bool IsZeroSize(this Type type)
        {
            var zeroSize = type.IsValueType && !type.IsPrimitive &&
                type.GetFields((BindingFlags)0x34).All(fi => fi.FieldType.IsZeroSize());
            return zeroSize;
        }
    }
}
