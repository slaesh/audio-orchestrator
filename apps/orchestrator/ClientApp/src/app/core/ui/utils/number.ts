/**
 * Clamps a number between a minimum and maximum value.
 */
export function clamp(value: number, [min, max]: [number, number]): number {
  return Math.min(Math.max(value, min), max);
}

/**
 * Rounds a number to the nearest step.
 */
export function roundToStep(value: number, min: number, step: number): number {
  const steps = Math.round((value - min) / step);
  return min + steps * step;
}

/**
 * Converts a value to a percentage based on min and max.
 */
export function convertValueToPercentage(value: number, min: number, max: number): number {
  return ((value - min) / (max - min)) * 100;
}
