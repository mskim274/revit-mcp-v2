using System;
using System.Collections.Generic;

namespace RevitMCP.CommandSet.Helpers
{
    /// <summary>
    /// Validation for values received directly over the raw WebSocket contract.
    /// The TypeScript/Zod layer normally enforces these rules, but commands must
    /// fail closed when invoked without that layer.
    /// </summary>
    internal static class RawParameterValidation
    {
        internal static bool TryGetRequiredFiniteDouble(
            Dictionary<string, object> parameters,
            string key,
            out double value,
            out string error)
        {
            value = 0;
            error = null;
            if (parameters == null || !parameters.TryGetValue(key, out var raw))
            {
                error = $"{key} is required and must be a finite number.";
                return false;
            }

            if (!TryGetFiniteNumericValue(raw, out value))
            {
                error = $"{key} must be a finite number.";
                return false;
            }

            return true;
        }

        internal static bool TryGetOptionalFiniteDouble(
            Dictionary<string, object> parameters,
            string key,
            double defaultValue,
            out double value,
            out string error)
        {
            value = defaultValue;
            error = null;
            if (parameters == null || !parameters.TryGetValue(key, out var raw))
                return true;

            if (!TryGetFiniteNumericValue(raw, out value))
            {
                error = $"{key} must be a finite number when supplied.";
                return false;
            }

            return true;
        }

        internal static bool TryGetOptionalStrictBool(
            Dictionary<string, object> parameters,
            string key,
            bool defaultValue,
            out bool value,
            out string error)
        {
            value = defaultValue;
            error = null;
            if (parameters == null || !parameters.TryGetValue(key, out var raw))
                return true;

            if (!(raw is bool boolValue))
            {
                error = $"{key} must be a boolean when supplied.";
                return false;
            }

            value = boolValue;
            return true;
        }

        internal static bool IsNonFiniteNumeric(object value)
        {
            return value is double doubleValue &&
                   (double.IsNaN(doubleValue) ||
                    double.IsInfinity(doubleValue))
                   || value is float floatValue &&
                   (float.IsNaN(floatValue) ||
                    float.IsInfinity(floatValue));
        }

        internal static bool TryConvertFiniteParameterDouble(
            object value,
            out double converted)
        {
            converted = 0;
            if (value == null)
                return false;
            try
            {
                converted = Convert.ToDouble(value);
                return !double.IsNaN(converted) &&
                       !double.IsInfinity(converted);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetFiniteNumericValue(
            object raw,
            out double value)
        {
            value = 0;
            switch (raw)
            {
                case byte byteValue:
                    value = byteValue;
                    break;
                case sbyte signedByteValue:
                    value = signedByteValue;
                    break;
                case short shortValue:
                    value = shortValue;
                    break;
                case ushort unsignedShortValue:
                    value = unsignedShortValue;
                    break;
                case int intValue:
                    value = intValue;
                    break;
                case uint unsignedIntValue:
                    value = unsignedIntValue;
                    break;
                case long longValue:
                    value = longValue;
                    break;
                case ulong unsignedLongValue:
                    value = unsignedLongValue;
                    break;
                case float floatValue:
                    value = floatValue;
                    break;
                case double doubleValue:
                    value = doubleValue;
                    break;
                case decimal decimalValue:
                    value = (double)decimalValue;
                    break;
                default:
                    return false;
            }

            return !double.IsNaN(value) &&
                   !double.IsInfinity(value);
        }
    }
}
