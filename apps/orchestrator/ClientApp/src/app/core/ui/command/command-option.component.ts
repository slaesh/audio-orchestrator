import {
  booleanAttribute,
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  ElementRef,
  inject,
  input,
  signal,
  ViewEncapsulation,
} from '@angular/core';

import { NgIcon, type IconName } from '@ng-icons/core';
import type { ClassValue } from 'clsx';

import type { ZardCommandOptionGroupComponent } from '@ui/command/command-option-group.component';
import { ZardCommandComponent } from '@ui/command/command.component';
import {
  commandItemVariants,
  commandShortcutVariants,
  type ZardCommandItemVariants,
} from '@ui/command/command.variants';
import { mergeClasses } from '@ui/utils';

@Component({
  selector: 'z-command-option',
  imports: [NgIcon],
  templateUrl: './command-option.component.html',
  styleUrl: './command-option.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
  encapsulation: ViewEncapsulation.None,
  exportAs: 'zCommandOption',
})
export class ZardCommandOptionComponent {
  private readonly elementRef = inject(ElementRef);
  private readonly parentCommandComponent = inject(ZardCommandComponent, { optional: true });

  readonly zValue = input.required<unknown>();
  readonly zLabel = input.required<string>();
  readonly zCommand = input<string>('');
  readonly zIcon = input<IconName>();
  readonly zShortcut = input<string>('');
  readonly zDisabled = input(false, { transform: booleanAttribute });
  readonly variant = input<ZardCommandItemVariants>('default');
  readonly class = input<ClassValue>('');
  readonly parentCommand = input<ZardCommandComponent | null>(null);
  readonly commandGroup = input<ZardCommandOptionGroupComponent | null>(null);

  readonly isSelected = signal(false);

  protected readonly classes = computed(() => {
    const baseClasses = commandItemVariants({ variant: this.variant() });
    const selectedClasses = this.isSelected() ? 'bg-accent text-accent-foreground' : '';
    return mergeClasses(baseClasses, selectedClasses, this.class());
  });

  protected readonly shortcutClasses = computed(() => mergeClasses(commandShortcutVariants()));

  private get commandComponent() {
    let parent = this.parentCommand();
    parent ||= this.parentCommandComponent;
    return parent;
  }

  protected readonly isOptionVisible = computed(() => {
    const parent = this.commandComponent;

    if (!parent) {
      return true;
    }
    /*
      If no search term, show this option, otherwise check
      if this option is included in the filtered list
     */
    return !parent.searchTerm() || parent.filteredOptions().includes(this);
  });

  constructor() {
    effect(onCleanup => {
      const cmd = this.parentCommand();
      const grp = this.commandGroup();

      if (cmd) {
        cmd.registerOption(this);
        onCleanup(() => cmd.unregisterOption(this));
      }

      if (grp) {
        grp.registerOption(this);
        onCleanup(() => grp.unregisterOption(this));
      }
    });
  }

  onClick() {
    if (this.zDisabled()) {
      return;
    }

    this.commandComponent?.selectOption(this);
  }

  onMouseEnter() {
    if (this.zDisabled()) {
      return;
    }
    // Visual feedback for hover
  }

  setSelected(selected: boolean) {
    this.isSelected.set(selected);
  }

  focus() {
    const element = this.elementRef.nativeElement;
    element.focus();
    element.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
  }
}
