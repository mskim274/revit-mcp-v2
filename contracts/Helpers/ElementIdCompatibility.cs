using System;

namespace Autodesk.Revit.DB
{
    /// <summary>
    /// Keeps the wire protocol on 64-bit element IDs while still compiling
    /// against Revit 2023/2024, whose ElementId API exposes only Int32.
    /// This helper is part of the stable host/CommandSet contract.
    /// </summary>
    public static class ElementIdCompatibility
    {
        public static long GetValue(this ElementId id)
        {
            if (id == null) return -1;
#if NET8_0_OR_GREATER
            return id.Value;
#else
            return id.IntegerValue;
#endif
        }

        public static ElementId Create(object rawValue)
        {
            if (rawValue == null)
                throw new ArgumentNullException(nameof(rawValue));
            var value = Convert.ToInt64(rawValue);
#if NET8_0_OR_GREATER
            return new ElementId(value);
#else
            if (value < int.MinValue || value > int.MaxValue)
                throw new ArgumentOutOfRangeException(
                    nameof(rawValue),
                    value,
                    "Revit 2023/2024 element IDs must fit in a signed 32-bit integer.");
            return new ElementId((int)value);
#endif
        }
    }
}
