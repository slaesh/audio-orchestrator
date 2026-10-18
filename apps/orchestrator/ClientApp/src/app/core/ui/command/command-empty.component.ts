import { ChangeDetectionStrategy, Component, computed, inject, input, ViewEncapsulation } from '@angular/core';

import type { ClassValue } from 'clsx';

import { ZardCommandComponent } from '@ui/command/command.component';
import { commandEmptyVariants } from '@ui/command/command.variants';
import { mergeClasses } from '@ui/utils';

@Component({
  selector: 'z-command-empty',
  templateUrl: './command-empty.component.html',
  styleUrl: './command-empty.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
  encapsulation: ViewEncapsulation.None,
  exportAs: 'zCommandEmpty',
})
export class ZardCommandEmptyComponent {
  private readonly commandComponent = inject(ZardCommandComponent, { optional: true });

  readonly class = input<ClassValue>('');

  protected readonly classes = computed(() => mergeClasses(commandEmptyVariants(), this.class()));

  protected readonly shouldShow = computed(() => {
    // Check traditional command component
    if (this.commandComponent) {
      const filteredOptions = this.commandComponent.filteredOptions();
      return filteredOptions.length === 0;
    }

    return false;
  });
}
