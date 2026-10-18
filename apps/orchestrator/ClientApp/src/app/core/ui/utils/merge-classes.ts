import { type ClassValue, clsx } from 'clsx';
import { twMerge } from 'tailwind-merge';

/**
 * Merges Tailwind CSS classes correctly using clsx and tailwind-merge.
 */
export function mergeClasses(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs));
}

/**
 * A no-operation function that does nothing.
 */
// eslint-disable-next-line @typescript-eslint/no-empty-function
export const noopFn = () => {};
