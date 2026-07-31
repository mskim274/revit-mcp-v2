import { z } from "zod";

export function idempotencyKeySchema(description?: string) {
  return z
    .string()
    .trim()
    .min(1)
    .max(512)
    .optional()
    .describe(
      description ??
        "Stable deduplication key for retries. Reuse the exact same key for the same logical operation within the 15-minute cache window."
    );
}

export type ElementIdInput = number | string;

export function elementIdSchema(description = "Revit ElementId") {
  return z
    .union([
      z
        .number()
        .int()
        .positive()
        .refine(Number.isSafeInteger, {
          message:
            "Numeric ElementIds must be JavaScript-safe integers; use a decimal string for larger 64-bit IDs.",
        }),
      z
        .string()
        .trim()
        .regex(/^[1-9]\d*$/, "ElementId strings must contain decimal digits only.")
        .refine(
          (value) =>
            value.length < 19 ||
            (value.length === 19 && value <= "9223372036854775807"),
          { message: "ElementId exceeds signed 64-bit range." }
        ),
    ])
    .describe(
      `${description}. Pass a safe integer or a decimal string for large 64-bit IDs.`
    );
}

const VALUE_MODE_DESCRIPTION =
  '"internal" (default) passes numeric double values in the parameter spec’s Revit internal units (length ft, area ft², volume ft³, angle radians). "display" parses through Revit project display units (for example "250 mm") using SetValueString.';

export const VALUE_MODE_OVERRIDE_SCHEMA = z
  .enum(["internal", "display"])
  .optional()
  .describe(VALUE_MODE_DESCRIPTION);

export const VALUE_MODE_SCHEMA = z
  .enum(["internal", "display"])
  .optional()
  .default("internal")
  .describe(VALUE_MODE_DESCRIPTION);
