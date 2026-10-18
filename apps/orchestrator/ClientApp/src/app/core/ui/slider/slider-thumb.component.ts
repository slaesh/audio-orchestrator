import { ChangeDetectionStrategy, Component, computed, ElementRef, inject, input, ViewEncapsulation } from '@angular/core';
import type { ClassValue } from 'clsx';
import { mergeClasses } from '@ui/utils';
import { sliderOrientationVariants, sliderThumbVariants } from './slider.variants';

@Component({
  selector: 'z-slider-thumb',
  standalone: true,
  template: ``,
  changeDetection: ChangeDetectionStrategy.OnPush,
  encapsulation: ViewEncapsulation.None,
  host: {
    '[class]': 'classes()',
    '[attr.role]': '"slider"',
    '[attr.aria-valuemin]': 'min()',
    '[attr.aria-valuemax]': 'max()',
    '[attr.aria-valuenow]': 'value()',
    '[attr.aria-disabled]': 'disabled() ? true : null',
    '[style.left]': 'orientation() === "horizontal" ? percent() + "%" : "50%"',
    '[style.top]': 'orientation() === "horizontal" ? "50%" : null',
    '[style.bottom]': 'orientation() === "vertical" ? percent() + "%" : null',
    '[style.transform]': 'orientation() === "horizontal" ? "translate(-50%, -50%)" : "translate(-50%, 50%)"',
    'tabindex': '0',
    'data-slot': 'slider-thumb',
  },
})
export class ZSliderThumbComponent {
  readonly value = input(0);
  readonly min = input(0);
  readonly max = input(100);
  readonly disabled = input(false);
  readonly percent = input(0);
  readonly offset = input(0);

  readonly orientation = input<'horizontal' | 'vertical'>('horizontal');
  readonly class = input<ClassValue>('');

  protected readonly classes = computed(() =>
    mergeClasses(sliderThumbVariants({ disabled: this.disabled() }), this.class()),
  );

  protected readonly orientationClasses = computed(() =>
    mergeClasses(sliderOrientationVariants({ zOrientation: this.orientation() })),
  );

  private readonly elementRef = inject(ElementRef<HTMLElement>);

  get nativeElement(): HTMLElement {
    return this.elementRef.nativeElement;
  }
}
