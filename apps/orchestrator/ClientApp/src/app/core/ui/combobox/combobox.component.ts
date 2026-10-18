import { NgTemplateOutlet } from '@angular/common';
import {
  afterNextRender,
  booleanAttribute,
  ChangeDetectionStrategy,
  Component,
  computed,
  ElementRef,
  forwardRef,
  inject,
  Injector,
  input,
  linkedSignal,
  output,
  runInInjectionContext,
  signal,
  viewChild,
  ViewEncapsulation,
} from '@angular/core';
import { type ControlValueAccessor, FormsModule, NG_VALUE_ACCESSOR } from '@angular/forms';

import { NgIcon, provideIcons, type IconName } from '@ng-icons/core';
import { lucideCheck, lucideChevronsUpDown } from '@ng-icons/lucide';
import type { ClassValue } from 'clsx';

import { ZardButtonComponent, type ZardButtonTypeVariants } from '@ui/button';
import { comboboxVariants, type ZardComboboxWidthVariants } from '@ui/combobox/combobox.variants';
import {
  ZardCommandComponent,
  ZardCommandEmptyComponent,
  ZardCommandInputComponent,
  ZardCommandListComponent,
  ZardCommandOptionComponent,
  ZardCommandOptionGroupComponent,
  type ZardCommandOption,
} from '@ui/command';
import { ZardEmptyComponent } from '@ui/empty';
import { ZardPopoverComponent, ZardPopoverDirective } from '@ui/popover';
import { mergeClasses } from '@ui/utils';

export interface ZardComboboxOption {
  value: string;
  label: string;
  disabled?: boolean;
  icon?: IconName;
}

export interface ZardComboboxGroup {
  label?: string;
  options: ZardComboboxOption[];
}

@Component({
  selector: 'z-combobox',
  imports: [
    FormsModule,
    NgTemplateOutlet,
    NgIcon,
    ZardButtonComponent,
    ZardCommandComponent,
    ZardCommandInputComponent,
    ZardCommandListComponent,
    ZardCommandEmptyComponent,
    ZardCommandOptionComponent,
    ZardCommandOptionGroupComponent,
    ZardPopoverDirective,
    ZardPopoverComponent,
    ZardEmptyComponent,
  ],
  templateUrl: './combobox.component.html',
  styleUrl: './combobox.component.css',
  providers: [
    {
      provide: NG_VALUE_ACCESSOR,
      useExisting: forwardRef(() => ZardComboboxComponent),
      multi: true,
    },
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  encapsulation: ViewEncapsulation.None,
  viewProviders: [provideIcons({ lucideChevronsUpDown, lucideCheck })],
  host: {
    '[class]': 'classes()',
    '(document:keydown.escape)': 'onDocumentKeyDown($event)',
    '(keydown.escape.prevent-with-stop)': 'onKeyDownEscape()',
    '(keydown.{arrowdown,arrowup,enter,home,end,pageup,pagedown,space}.prevent)': 'onKeyDown($event)',
    '(keydown.tab)': 'onKeyDown($event)',
  },
  exportAs: 'zCombobox',
})
export class ZardComboboxComponent implements ControlValueAccessor {
  private readonly injector = inject(Injector);

  readonly class = input<ClassValue>('');
  readonly buttonVariant = input<ZardButtonTypeVariants>('outline');
  readonly zWidth = input<ZardComboboxWidthVariants>('default');
  readonly placeholder = input<string>('Select...');
  readonly searchPlaceholder = input<string>('Search...');
  readonly emptyText = input<string>('No results found.');
  readonly zDisabled = input(false, { transform: booleanAttribute });
  readonly searchable = input(true, { transform: booleanAttribute });
  readonly value = input<string | null>(null);
  readonly options = input<ZardComboboxOption[]>([]);
  readonly groups = input<ZardComboboxGroup[]>([]);
  readonly ariaLabel = input<string>('');
  readonly ariaDescribedBy = input<string>('');

  readonly zValueChange = output<string | null>();
  readonly zComboSelected = output<ZardComboboxOption>();

  readonly popoverDirective = viewChild.required('popoverTrigger', { read: ZardPopoverDirective });
  readonly buttonRef = viewChild.required('popoverTrigger', { read: ElementRef });
  readonly commandRef = viewChild('commandRef', { read: ZardCommandComponent });
  readonly commandInputRef = viewChild('commandInputRef', { read: ZardCommandInputComponent });

  protected readonly disabledState = linkedSignal(() => this.zDisabled());
  protected readonly internalValue = signal<string | null>(null);
  protected readonly open = signal(false);

  protected readonly classes = computed(() =>
    mergeClasses(
      comboboxVariants({
        zWidth: this.zWidth(),
      }),
      this.class(),
    ),
  );

  protected readonly buttonClasses = computed(() => 'w-full justify-between');

  protected readonly popoverClasses = computed(() => {
    const widthClass = this.zWidth() === 'full' ? 'w-full' : comboboxVariants({ zWidth: this.zWidth() });
    return `${widthClass} p-0`;
  });

  protected readonly currentValue = computed(() => this.value() ?? this.internalValue());

  protected readonly displayValue = computed(() => {
    const currentValue = this.currentValue();
    if (!currentValue) {
      return null;
    }

    // Search in groups first
    if (this.groups().length) {
      for (const group of this.groups()) {
        const option = group.options.find(opt => opt.value === currentValue);
        if (option) {
          return option.label;
        }
      }
    }

    // Then search in flat options
    const option = this.options().find(opt => opt.value === currentValue);
    return option?.label ?? null;
  });

  private onChange: (value: string | null) => void = () => {
    // ControlValueAccessor implementation
  };

  private onTouched: () => void = () => {
    // ControlValueAccessor implementation
  };

  setOpen(open: boolean) {
    this.open.set(open);
    if (open) {
      runInInjectionContext(this.injector, () =>
        afterNextRender(() => {
          const commandRef = this.commandRef();
          if (commandRef) {
            // Refresh options to ensure they're detected
            commandRef.refreshOptions();
            // Focus on search input if searchable, otherwise on command component
            if (this.searchable()) {
              this.commandInputRef()?.focus();
            } else {
              commandRef.focus();
            }
          }
        }),
      );
    }
  }

  handleSelect(commandOption: ZardCommandOption) {
    const selectedValue = commandOption.value as string;

    // Toggle behavior - if same value is selected, clear it
    const newValue = selectedValue === this.currentValue() ? null : selectedValue;

    this.internalValue.set(newValue);
    this.onChange(newValue);
    this.zValueChange.emit(newValue);

    // Emit the combobox option if we have a selection
    if (newValue) {
      let selectedOption: ZardComboboxOption | undefined;

      if (this.groups().length > 0) {
        for (const group of this.groups()) {
          selectedOption = group.options.find(opt => opt.value === newValue);
          if (selectedOption) {
            break;
          }
        }
      } else {
        selectedOption = this.options().find(opt => opt.value === newValue);
      }

      if (selectedOption) {
        this.zComboSelected.emit(selectedOption);
      }
    }

    // Close the popover
    this.popoverDirective().hide();

    // Return focus to the combobox button after selection
    this.buttonRef().nativeElement.focus();
  }

  onKeyDownEscape(): void {
    if (this.open()) {
      this.popoverDirective().hide();
      this.buttonRef().nativeElement.focus();
    } else if (this.currentValue()) {
      this.internalValue.set(null);
      this.onChange(null);
      this.zValueChange.emit(null);
    }
  }

  onKeyDown(e: Event) {
    if (this.disabledState()) {
      return;
    }

    const { key, ctrlKey, altKey, metaKey } = e as KeyboardEvent;

    // Handle different keyboard events based on combobox state
    if (this.open()) {
      // When popover is open
      switch (key) {
        case 'Tab':
          // Allow tab to close and move to next element
          this.popoverDirective().hide();
          break;
        case 'ArrowDown':
        case 'ArrowUp':
        case 'Enter':
        case 'Home':
        case 'End':
        case 'PageUp':
        case 'PageDown':
          // Forward navigation to command component
          this.commandRef()?.onKeyDown(e as KeyboardEvent);
          break;
      }
    } else {
      // When popover is closed
      switch (key) {
        case 'ArrowDown':
        case 'ArrowUp':
        case 'Enter':
        case ' ': // Space key
          this.popoverDirective().show();
          break;
        default:
          // For searchable comboboxes, open and start typing
          if (this.searchable() && key.length === 1 && !ctrlKey && !altKey && !metaKey) {
            this.popoverDirective().show();
            // Let the command input handle the character after opening
            runInInjectionContext(this.injector, () =>
              afterNextRender(() => {
                const inputElement = this.commandInputRef();
                if (inputElement) {
                  inputElement.searchInput().nativeElement.value = key;
                  inputElement.updateParentComponents(key);
                  inputElement.focus();
                }
              }),
            );
          }
          break;
      }
    }
  }

  // needed when component loses focus by keyboard.
  onDocumentKeyDown(event: Event) {
    // Close on Escape from anywhere when this combobox is open
    if (this.open()) {
      const target = event.target as Element;
      const buttonElement = this.buttonRef().nativeElement;
      // Only handle if not already handled by the component itself
      if (!buttonElement.contains(target)) {
        this.popoverDirective().hide();
        this.buttonRef().nativeElement.focus();
      }
    }
  }

  // ControlValueAccessor implementation
  writeValue(value: string | null): void {
    this.internalValue.set(value);
  }

  registerOnChange(fn: (value: string | null) => void): void {
    this.onChange = fn;
  }

  registerOnTouched(fn: () => void): void {
    this.onTouched = fn;
  }

  setDisabledState(isDisabled: boolean): void {
    this.disabledState.set(isDisabled);
  }
}
