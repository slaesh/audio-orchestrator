import { ChangeDetectionStrategy, Component, computed, ElementRef, input, ViewEncapsulation, viewChild } from '@angular/core';
import type { ClassValue } from 'clsx';
import { mergeClasses } from '@ui/utils';
import { sliderTrackVariants } from './slider.variants';

@Component({
  selector: 'z-slider-track',
  standalone: true,
  template: `
    <span #track data-slot="slider-track" [attr.data-orientation]="orientation()" [class]="classes()">
      <ng-content />
    </span>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
  encapsulation: ViewEncapsulation.None,
  host: {
    '[class]': '"data-[orientation=horizontal]:w-full data-[orientation=vertical]:h-full"',
    '[attr.data-orientation]': 'orientation()',
  },
})
export class ZSliderTrackComponent {
  readonly orientation = input<'horizontal' | 'vertical'>('horizontal');
  readonly class = input<ClassValue>('');

  protected readonly classes = computed(() =>
    mergeClasses(sliderTrackVariants({ zOrientation: this.orientation() }), this.class()),
  );

  private readonly trackEl = viewChild.required<ElementRef<HTMLElement>>('track');

  get nativeElement(): HTMLElement {
    return this.trackEl().nativeElement;
  }
}
