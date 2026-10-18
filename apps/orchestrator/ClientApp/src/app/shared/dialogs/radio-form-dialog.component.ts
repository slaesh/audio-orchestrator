import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideX, lucideRadio } from '@ng-icons/lucide';
import { ZardButtonComponent } from '@ui/button/button.component';
import { ZardInputDirective } from '@ui/input/input.directive';

export interface RadioDialogData {
  title: string;
  name: string;
  url: string;
  mode: 'create' | 'edit';
}

@Component({
  selector: 'app-radio-form-dialog',
  standalone: true,
  imports: [CommonModule, FormsModule, NgIcon, ZardButtonComponent, ZardInputDirective],
  template: `
    <div class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-background/80 backdrop-blur-sm animate-in fade-in duration-200" (click)="ref.close()">
      <div class="relative w-full max-w-[500px] overflow-hidden rounded-2xl border border-border bg-card p-6 shadow-2xl animate-in zoom-in-95 duration-200" (click)="$event.stopPropagation()">
        
        <div class="flex items-center justify-between mb-8">
          <div class="flex items-center gap-3">
            <div class="size-10 rounded-xl bg-primary/10 text-primary flex items-center justify-center">
              <ng-icon name="lucideRadio" size="1.25rem" />
            </div>
            <div>
              <h2 class="text-xl font-bold tracking-tight">{{ data.title }}</h2>
            </div>
          </div>
          <button (click)="ref.close()" class="p-1 rounded-md hover:bg-muted transition-colors opacity-50 hover:opacity-100">
            <ng-icon name="lucideX" />
          </button>
        </div>

        <div class="space-y-6">
          <div class="space-y-2">
            <label class="text-[0.7rem] uppercase font-bold tracking-wider text-muted-foreground/70 ml-1">Station Name</label>
            <input 
              z-input
              [(ngModel)]="formData.name" 
              placeholder="e.g. SWR3, Sunshine Live..."
              class="w-full"
              autofocus
            />
          </div>

          <div class="space-y-2">
            <label class="text-[0.7rem] uppercase font-bold tracking-wider text-muted-foreground/70 ml-1">Stream URL (Icecast/HE-AAC/MP3)</label>
            <input 
              z-input
              [(ngModel)]="formData.url" 
              placeholder="http://..."
              (keydown.enter)="save()"
              class="w-full"
            />
            <p class="text-[10px] text-muted-foreground italic ml-1 leading-relaxed">
              Updating the URL will briefly interrupt the stream to restart the audio bridge.
            </p>
          </div>

        </div>

        <div class="flex flex-col-reverse sm:flex-row sm:justify-end gap-3 mt-10">
          <z-button zType="outline" (click)="ref.close()" class="sm:px-8">
            Cancel
          </z-button>
          <z-button 
            zType="default"
            (click)="save()"
            [zDisabled]="!formData.name || !formData.url"
            class="sm:px-8"
          >
            {{ data.mode === 'create' ? 'Add Station' : 'Save Changes' }}
          </z-button>
        </div>
      </div>
    </div>
  `,
  viewProviders: [provideIcons({ lucideX, lucideRadio })]
})
export class RadioFormDialogComponent {
  public data = inject<RadioDialogData>(DIALOG_DATA);
  public ref = inject(DialogRef);

  formData = {
    name: this.data.name || '',
    url: this.data.url || ''
  };

  save() {
    if (this.formData.name && this.formData.url) {
      this.ref.close(this.formData);
    }
  }
}
