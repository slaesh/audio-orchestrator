import { DOCUMENT } from '@angular/common';
import {
  type AfterViewInit,
  booleanAttribute,
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  ElementRef,
  forwardRef,
  inject,
  input,
  linkedSignal,
  numberAttribute,
  type OnDestroy,
  output,
  signal,
  viewChild,
  ViewEncapsulation,
} from '@angular/core';
import { type ControlValueAccessor, NG_VALUE_ACCESSOR } from '@angular/forms';

import type { ClassValue } from 'clsx';
import { filter, fromEvent, map, merge, Subject, switchMap, takeUntil, tap } from 'rxjs';

import { mergeClasses } from '@ui/utils';
import { clamp, convertValueToPercentage, roundToStep } from '@ui/utils';

import { sliderVariants } from './slider.variants';
import { ZSliderTrackComponent } from './slider-track.component';
import { ZSliderRangeComponent } from './slider-range.component';
import { ZSliderThumbComponent } from './slider-thumb.component';

@Component({
  selector: 'z-slider',
  imports: [ZSliderTrackComponent, ZSliderRangeComponent, ZSliderThumbComponent],
  standalone: true,
  templateUrl: './slider.component.html',
  styleUrl: './slider.component.css',
  providers: [
    {
      provide: NG_VALUE_ACCESSOR,
      useExisting: forwardRef(() => ZardSliderComponent),
      multi: true,
    },
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  encapsulation: ViewEncapsulation.None,
  host: {
    '[class]': 'classes()',
    '[attr.data-orientation]': 'zOrientation()',
    '[attr.aria-disabled]': 'disabled() ? true : null',
    '[attr.data-disabled]': 'disabled() ? true : null',
    '(pointerdown)': '$event.stopPropagation()',
    '(mousedown)': '$event.stopPropagation()',
    '(touchstart)': '$event.stopPropagation()'
  },
  exportAs: 'zSlider',
})
export class ZardSliderComponent implements ControlValueAccessor, AfterViewInit, OnDestroy {
  readonly zMin = input(0, { transform: numberAttribute });
  readonly zMax = input(100, { transform: numberAttribute });
  readonly zDefault = input(0, { transform: numberAttribute });
  readonly zValue = input<number | null, unknown>(null, {
    transform: (v: unknown) => (v === null || v === undefined ? null : numberAttribute(v)),
  });
  readonly zStep = input(1, { transform: numberAttribute });
  readonly zDisabled = input(false, { transform: booleanAttribute });

  readonly zOrientation = input<'horizontal' | 'vertical'>('horizontal');
  readonly class = input<ClassValue>('');

  readonly zSlideIndexChange = output<number>();

  readonly thumbRef = viewChild.required(ZSliderThumbComponent);
  readonly trackRef = viewChild.required(ZSliderTrackComponent);

  private elementRef = inject<ElementRef<HTMLElement>>(ElementRef);
  private document = inject(DOCUMENT);

  protected readonly classes = computed(() =>
    mergeClasses(sliderVariants({ orientation: this.zOrientation() }), this.class()),
  );

  protected readonly disabled = linkedSignal(() => this.zDisabled());

  readonly percentValue = signal(50);
  readonly lastEmittedValue = signal(0);

  readonly thumbOffset = signal(0);

  // eslint-disable-next-line @typescript-eslint/no-empty-function
  private onTouched: () => void = () => {};
  // eslint-disable-next-line @typescript-eslint/no-empty-function
  private onChange: (value: number) => void = () => {};

  private isDragging = false;
  private destroy$ = new Subject<void>();

  constructor() {
    effect(() => {
      const value = this.zValue();
      if (this.isDragging) return;
      if (value !== null && !isNaN(value) && value !== this.lastEmittedValue()) {
        this.setInitialValue();
      }
    });
  }

  ngOnDestroy() {
    this.destroy$.next();
    this.destroy$.complete();
  }

  ngAfterViewInit() {
    const pointerDown$ = fromEvent<PointerEvent>(this.elementRef.nativeElement, 'pointerdown').pipe(
      filter(() => !this.disabled()),
      tap(event => {
        this.isDragging = true;
        const target = event.target as HTMLElement;
        const isThumb = this.thumbRef().nativeElement.contains(target);
        const isTrack = this.trackRef().nativeElement.contains(target);

        if (isTrack && !isThumb) {
          const coord = this.zOrientation() === 'vertical' ? event.clientY : event.clientX;
          const clickPercentage = this.calculatePercentage(coord);
          this.updateSliderFromPercentage(clickPercentage);
          this.onTouched();
          requestAnimationFrame(() => {
            this.thumbRef().nativeElement.focus();
          });
        }
      }),
    );

    const pointerMove$ = fromEvent<PointerEvent>(this.document, 'pointermove');
    const pointerUp$ = merge(
      fromEvent<PointerEvent>(this.document, 'pointerup'),
      fromEvent<PointerEvent>(this.document, 'pointercancel'),
    ).pipe(
      tap(() => {
        this.isDragging = false;
      }),
    );

    pointerDown$
      .pipe(
        switchMap(() =>
          pointerMove$.pipe(
            takeUntil(pointerUp$),
            map(event => {
              const coord = this.zOrientation() === 'vertical' ? event.clientY : event.clientX;
              return this.calculatePercentage(coord);
            }),
          ),
        ),
        takeUntil(this.destroy$),
      )
      .subscribe(percentage => {
        if (this.disabled()) {
          return;
        }
        this.updateSliderFromPercentage(percentage);
        this.onTouched();
      });

    this.setInitialValue();
  }

  writeValue(value: number): void {
    if (this.isDragging) return;
    if (value == null) {
      this.setInitialValue();
      return;
    }

    const min = this.zMin();
    const max = this.zMax();
    const step = this.zStep();

    const clampedValue = clamp(value, [min, max]);
    const roundedValue = roundToStep(clampedValue, min, step);

    if (roundedValue === this.lastEmittedValue()) {
      return;
    }

    this.percentValue.set(convertValueToPercentage(roundedValue, min, max));
    this.lastEmittedValue.set(roundedValue);
  }

  registerOnChange(fn: (value: number) => void): void {
    this.onChange = fn;
  }

  registerOnTouched(fn: () => void): void {
    this.onTouched = fn;
  }

  setDisabledState(isDisabled: boolean): void {
    this.disabled.set(isDisabled);
  }

  handleKeydown(event: Event): void {
    if (this.disabled()) {
      return;
    }

    const percent = this.percentValue();
    const rawValue = this.zMin() + ((this.zMax() - this.zMin()) * percent) / 100;
    const currentValue = roundToStep(rawValue, this.zMin(), this.zStep());

    let newValue = currentValue;

    const { key } = event as KeyboardEvent;

    switch (key) {
      case 'Home':
        newValue = this.zMin();
        break;
      case 'End':
        newValue = this.zMax();
        break;
      case 'ArrowLeft':
      case 'ArrowDown':
        newValue = Math.max(currentValue - this.zStep(), this.zMin());
        break;
      case 'ArrowRight':
      case 'ArrowUp':
        newValue = Math.min(currentValue + this.zStep(), this.zMax());
        break;

      default:
        return;
    }

    if (newValue !== currentValue) {
      this.percentValue.set(convertValueToPercentage(newValue, this.zMin(), this.zMax()));
      this.zSlideIndexChange.emit(newValue);
      this.lastEmittedValue.set(newValue);
      this.onChange(newValue);
    }
  }

  private updateSliderFromPercentage(percentage: number): void {
    const clamped = clamp(percentage, [0, 1]);
    const rawValue = this.zMin() + (this.zMax() - this.zMin()) * clamped;
    const value = roundToStep(rawValue, this.zMin(), this.zStep());

    if (value !== this.lastEmittedValue()) {
      this.percentValue.set(convertValueToPercentage(value, this.zMin(), this.zMax()));
      this.zSlideIndexChange.emit(value);
      this.lastEmittedValue.set(value);
      this.onChange(value);
    }
  }

  private calculatePercentage(clientCoord: number): number {
    const rect = this.elementRef.nativeElement.getBoundingClientRect();
    if (this.zOrientation() === 'vertical') {
      const relativeY = (clientCoord - rect.top) / rect.height;
      return clamp(1 - relativeY, [0, 1]);
    }
    const relativeX = (clientCoord - rect.left) / rect.width;
    return clamp(relativeX, [0, 1]);
  }

  private setInitialValue(): void {
    const min = this.zMin();
    const max = this.zMax();
    const step = this.zStep();

    const def = clamp(this.zDefault(), [min, max]);
    const raw = this.zValue();
    const value = raw != null && raw >= min && raw <= max ? raw : def;

    const initial = roundToStep(value, min, step);
    this.percentValue.set(convertValueToPercentage(initial, min, max));
    this.lastEmittedValue.set(initial);
    this.thumbOffset.set(0);
  }
}
