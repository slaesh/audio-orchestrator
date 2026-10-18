import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideX, lucideSettings } from '@ng-icons/lucide';
import { ZardButtonComponent } from '@ui/button/button.component';
import { ZardInputDirective } from '@ui/input/input.directive';

export interface RenameDialogData {
  title: string;
  subtitle?: string;
  label: string;
  initialValue: string;
  placeholder?: string;
}

@Component({
  selector: 'app-rename-dialog',
  standalone: true,
  imports: [CommonModule, FormsModule, NgIcon, ZardButtonComponent, ZardInputDirective],
  template: `
    <div class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-background/80 backdrop-blur-sm animate-in fade-in duration-200" (click)="ref.close()">
      <div class="relative w-full max-w-[450px] overflow-hidden rounded-2xl border border-border bg-card p-6 shadow-2xl animate-in zoom-in-95 duration-200" (click)="$event.stopPropagation()">
        
        <div class="flex items-center justify-between mb-8">
          <div class="flex items-center gap-3">
            <div class="size-10 rounded-xl bg-primary/10 text-primary flex items-center justify-center">
              <ng-icon name="lucideSettings" size="1.25rem" />
            </div>
            <div>
              <h2 class="text-xl font-bold tracking-tight">{{ data.title }}</h2>
              @if (data.subtitle) {
                <p class="text-xs text-muted-foreground uppercase tracking-widest font-bold opacity-50">{{ data.subtitle }}</p>
              }
            </div>
          </div>
          <button (click)="ref.close()" class="p-1 rounded-md hover:bg-muted transition-colors opacity-50 hover:opacity-100">
            <ng-icon name="lucideX" />
          </button>
        </div>

        <div class="space-y-6">
          <div class="space-y-2">
            <label class="text-[0.7rem] uppercase font-bold tracking-wider text-muted-foreground/70 ml-1">{{ data.label }}</label>
            <input 
              z-input
              [(ngModel)]="value" 
              [placeholder]="data.placeholder || 'Enter name...'"
              (keydown.enter)="save()"
              class="w-full"
              autofocus
            />
          </div>
        </div>

        <div class="flex flex-col-reverse sm:flex-row sm:justify-end gap-3 mt-10">
          <z-button zType="outline" (click)="ref.close()" class="sm:px-8">
            Cancel
          </z-button>
          <z-button 
            zType="default"
            (click)="save()"
            [zDisabled]="!value"
            class="sm:px-8"
          >
            Save Changes
          </z-button>
        </div>
      </div>
    </div>
  `,
  viewProviders: [provideIcons({ lucideX, lucideSettings })]
})
export class RenameDialogComponent {
  public data = inject<RenameDialogData>(DIALOG_DATA);
  public ref = inject(DialogRef);

  value = this.data.initialValue || '';

  save() {
    if (this.value) {
      this.ref.close(this.value);
    }
  }
}
