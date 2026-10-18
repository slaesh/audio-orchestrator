import {
  Directive,
  Input,
  OnChanges,
  SimpleChanges,
  TemplateRef,
  ViewContainerRef,
  inject,
} from '@angular/core';

@Directive({
  selector: '[zStringTemplateOutlet]',
  standalone: true,
})
export class ZardStringTemplateOutletDirective implements OnChanges {
  @Input() zStringTemplateOutlet: string | TemplateRef<any> | null = null;
  @Input() zStringTemplateOutletContext: any = null;

  private readonly templateRef = inject(TemplateRef<any>, { optional: true });
  private readonly viewContainer = inject(ViewContainerRef);

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['zStringTemplateOutlet']) {
      this.viewContainer.clear();
      if (!this.zStringTemplateOutlet) return;

      if (this.zStringTemplateOutlet instanceof TemplateRef) {
        this.viewContainer.createEmbeddedView(
          this.zStringTemplateOutlet,
          this.zStringTemplateOutletContext,
        );
      } else if (this.templateRef) {
        this.viewContainer.createEmbeddedView(
          this.templateRef,
          this.zStringTemplateOutletContext,
        );
      }
    }
  }
}
