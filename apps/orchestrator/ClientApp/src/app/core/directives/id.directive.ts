import { Directive, computed, input } from '@angular/core';

let uniqueId = 0;

@Directive({
  selector: '[zardId]',
  standalone: true,
  exportAs: 'zardId',
})
export class ZardIdDirective {
  readonly zardId = input<string>('');
  private readonly uniqueSuffix = ++uniqueId;
  
  readonly id = computed(() => `zard-${this.zardId() || 'id'}-${this.uniqueSuffix}`);
}
