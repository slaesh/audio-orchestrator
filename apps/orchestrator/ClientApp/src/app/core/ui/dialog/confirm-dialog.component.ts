import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideTriangleAlert, lucideX } from '@ng-icons/lucide';
import { ZardButtonComponent } from '@ui/button/button.component';
import { ZardInputDirective } from '@ui/input/input.directive';

export interface ConfirmOptions {
  title: string;
  message: string;
  confirmLabel?: string;
  cancelLabel?: string;
  confirmText?: string;
  variant?: 'default' | 'destructive';
}

@Component({
  selector: 'z-confirm-dialog',
  standalone: true,
  imports: [CommonModule, FormsModule, NgIcon, ZardButtonComponent, ZardInputDirective],
  template: `
    <div class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-background/80 backdrop-blur-sm animate-in fade-in duration-200" (click)="ref.close(false)">
      <div class="relative w-full max-w-[400px] overflow-hidden rounded-2xl border border-border bg-card p-6 shadow-2xl animate-in zoom-in-95 duration-200" (click)="$event.stopPropagation()">
        
        <!-- Header -->
        <div class="flex flex-col gap-2 mb-6">
          <div class="flex items-center justify-between">
            <h2 class="text-lg font-bold tracking-tight">{{ data.title }}</h2>
            <button (click)="ref.close(false)" class="p-1 rounded-md hover:bg-muted transition-colors opacity-50 hover:opacity-100">
              <ng-icon name="lucideX" />
            </button>
          </div>
          <p class="text-sm text-muted-foreground leading-relaxed">
            {{ data.message }}
          </p>
        </div>

        <!-- Confirm Text Input -->
        <div *ngIf="data.confirmText" class="mb-6 space-y-2">
          <p class="text-[10px] font-bold uppercase tracking-wider text-muted-foreground/60">
            Type <span class="text-foreground select-all">{{ data.confirmText }}</span> to proceed
          </p>
          <input 
            z-input
            [(ngModel)]="userInput" 
            placeholder="Type confirmation text..."
            (keydown.enter)="isMatch() && ref.close(true)"
            class="w-full"
            autofocus
          />
        </div>

        <!-- Footer -->
        <div class="flex flex-col-reverse sm:flex-row sm:justify-end gap-3 mt-8">
          <z-button zType="outline" (click)="ref.close(false)" class="sm:flex-1">
            {{ data.cancelLabel || 'Cancel' }}
          </z-button>
          <z-button 
            [zType]="data.variant === 'destructive' ? 'destructive' : 'default'" 
            (click)="ref.close(true)"
            [zDisabled]="!isMatch()"
            class="sm:flex-1"
          >
            {{ data.confirmLabel || 'Confirm' }}
          </z-button>
        </div>
      </div>
    </div>
  `,
  viewProviders: [provideIcons({ lucideTriangleAlert, lucideX })]
})
export class ConfirmDialogComponent {
  public data = inject<ConfirmOptions>(DIALOG_DATA);
  public ref = inject(DialogRef);

  userInput = '';

  isMatch(): boolean {
    if (!this.data.confirmText) return true;
    return this.userInput.toLowerCase() === this.data.confirmText.toLowerCase();
  }
}
