import {
  ChangeDetectionStrategy,
  Component,
  computed,
  input,
  ViewEncapsulation,
} from '@angular/core';

import { mergeClasses } from '@ui/utils';
import { popoverVariants } from './popover.variants';

@Component({
  selector: 'z-popover',
  imports: [],
  standalone: true,
  templateUrl: './popover.component.html',
  styleUrl: './popover.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '[class]': 'classes()',
  },
})
export class ZardPopoverComponent {
  readonly class = input<string>('');

  protected readonly classes = computed(() => mergeClasses(popoverVariants(), this.class()));
}
