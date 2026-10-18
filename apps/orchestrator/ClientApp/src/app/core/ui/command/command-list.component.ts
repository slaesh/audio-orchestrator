import { ChangeDetectionStrategy, Component, computed, input, ViewEncapsulation } from '@angular/core';

import type { ClassValue } from 'clsx';

import { commandListVariants } from '@ui/command/command.variants';
import { mergeClasses } from '@ui/utils';

@Component({
  selector: 'z-command-list',
  templateUrl: './command-list.component.html',
  styleUrl: './command-list.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
  encapsulation: ViewEncapsulation.None,
  exportAs: 'zCommandList',
})
export class ZardCommandListComponent {
  readonly class = input<ClassValue>('');

  protected readonly classes = computed(() => mergeClasses(commandListVariants(), this.class()));
}
