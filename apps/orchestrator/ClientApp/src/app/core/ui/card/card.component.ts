import {
  ChangeDetectionStrategy,
  Component,
  computed,
  input,
  output,
  type TemplateRef,
  viewChild,
  ViewEncapsulation,
} from '@angular/core';

import type { ClassValue } from 'clsx';

import { ZardButtonComponent } from '@ui/button';
import { ZardIdDirective } from '../../directives/id.directive';
import { ZardStringTemplateOutletDirective } from '../../directives/string-template-outlet/string-template-outlet.directive';
import { mergeClasses } from '@ui/utils';

import { cardBodyVariants, cardFooterVariants, cardHeaderVariants, cardVariants } from './card.variants';

@Component({
  selector: 'z-card',
  imports: [ZardStringTemplateOutletDirective, ZardButtonComponent, ZardIdDirective],
  templateUrl: './card.component.html',
  styleUrl: './card.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
  encapsulation: ViewEncapsulation.None,
  host: {
    'data-slot': 'card',
    '[class]': 'classes()',
    '[attr.aria-labelledby]': 'titleId()',
    '[attr.aria-describedby]': 'descriptionId()',
  },
  exportAs: 'zCard',
})
export class ZardCardComponent {
  private readonly generatedId = viewChild<ZardIdDirective>('z');

  readonly class = input<ClassValue>('');
  readonly zFooterBorder = input(false);
  readonly zHeaderBorder = input(false);
  readonly zAction = input('');
  readonly zDescription = input<string | TemplateRef<void>>();
  readonly zTitle = input<string | TemplateRef<void>>();

  readonly zActionClick = output<void>();

  protected readonly titleId = computed(() => {
    const baseId = this.generatedId()?.id();
    return this.zTitle() && baseId ? `${baseId}-title` : null;
  });

  protected readonly descriptionId = computed(() => {
    const baseId = this.generatedId()?.id();
    return this.zDescription() && baseId ? `${baseId}-description` : null;
  });

  protected readonly classes = computed(() => mergeClasses(cardVariants(), this.class()));
  protected readonly bodyClasses = computed(() => mergeClasses(cardBodyVariants()));
  protected readonly footerClasses = computed(() =>
    mergeClasses(cardFooterVariants(), this.zFooterBorder() ? 'border-t' : ''),
  );

  protected readonly headerClasses = computed(() =>
    mergeClasses(cardHeaderVariants(), this.zHeaderBorder() ? 'border-b' : ''),
  );

  protected onClick(): void {
    this.zActionClick.emit();
  }
}
