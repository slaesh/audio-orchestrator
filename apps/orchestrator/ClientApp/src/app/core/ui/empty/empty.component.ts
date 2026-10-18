import { NgOptimizedImage } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  input,
  type TemplateRef,
  ViewEncapsulation,
} from '@angular/core';

import { NgIcon } from '@ng-icons/core';
import type { ClassValue } from 'clsx';

import { ZardStringTemplateOutletDirective } from 'src/app/core/directives/string-template-outlet/string-template-outlet.directive';
import { mergeClasses } from '@ui/utils';

import {
  emptyActionsVariants,
  emptyDescriptionVariants,
  emptyHeaderVariants,
  emptyIconVariants,
  emptyImageVariants,
  emptyTitleVariants,
  emptyVariants,
} from './empty.variants';

@Component({
  selector: 'z-empty',
  imports: [NgOptimizedImage, NgIcon, ZardStringTemplateOutletDirective],
  templateUrl: './empty.component.html',
  styleUrl: './empty.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
  encapsulation: ViewEncapsulation.None,
  host: {
    '[class]': 'classes()',
  },
  exportAs: 'zEmpty',
})
export class ZardEmptyComponent {
  readonly zActions = input<TemplateRef<void>[]>([]);
  readonly zIcon = input<string>();
  readonly zImage = input<string | TemplateRef<void>>();
  readonly zTitle = input<string | TemplateRef<void>>();
  readonly zDescription = input<string | TemplateRef<void>>();
  readonly class = input<ClassValue>('');

  protected readonly classes = computed(() => mergeClasses(emptyVariants(), this.class()));
  protected readonly headerClasses = computed(() => emptyHeaderVariants());
  protected readonly imageClasses = computed(() => emptyImageVariants());
  protected readonly iconClasses = computed(() => emptyIconVariants());
  protected readonly titleClasses = computed(() => emptyTitleVariants());
  protected readonly descriptionClasses = computed(() => emptyDescriptionVariants());
  protected readonly actionsClasses = computed(() => emptyActionsVariants());
}
