import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideX, lucideMusic, lucideSettings } from '@ng-icons/lucide';
import { ZardButtonComponent } from '../../core/ui/button/button.component';
import { ZardInputDirective } from '../../core/ui/input/input.directive';
import { SpotifySourceDto } from '@api';

export interface SpotifySourceDialogData {
  mode: 'create' | 'edit';
  source?: SpotifySourceDto;
}

@Component({
  selector: 'app-spotify-source-form-dialog',
  standalone: true,
  imports: [CommonModule, FormsModule, NgIcon, ZardButtonComponent, ZardInputDirective],
  template: `
    <div class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-background/80 backdrop-blur-sm animate-in fade-in duration-200" (click)="ref.close()">
      <div class="relative w-full max-w-[450px] overflow-hidden rounded-2xl border border-border bg-card p-6 shadow-2xl animate-in zoom-in-95 duration-200" (click)="$event.stopPropagation()">
        
        <div class="flex items-center justify-between mb-8">
          <div class="flex items-center gap-3">
            <div class="size-10 rounded-xl bg-primary/10 text-primary flex items-center justify-center">
              <ng-icon [name]="data.mode === 'create' ? 'lucideMusic' : 'lucideSettings'" size="1.25rem" />
            </div>
            <div>
              <h2 class="text-xl font-bold tracking-tight">
                {{ data.mode === 'create' ? 'Add Spotify Source' : 'Edit Spotify Source' }}
              </h2>
            </div>
          </div>
          <button (click)="ref.close()" class="p-1 rounded-md hover:bg-muted transition-colors opacity-50 hover:opacity-100">
            <ng-icon name="lucideX" />
          </button>
        </div>

        <div class="space-y-6">
          <div class="space-y-2">
            <label class="text-[0.7rem] uppercase font-bold tracking-wider text-muted-foreground/70 ml-1">Source Name</label>
            <input 
              z-input
              [(ngModel)]="name" 
              placeholder="e.g. Living Room, Party Box..."
              class="w-full"
              autofocus
              (keydown.enter)="save()"
            />
            <p class="text-[10px] text-muted-foreground italic ml-1 leading-relaxed mb-4">
              This name will be visible in your Spotify app as a Connect device.
            </p>
          </div>

          <div class="space-y-2">
            <label class="text-[0.7rem] uppercase font-bold tracking-wider text-muted-foreground/70 ml-1">Startup Volume Limit (%)</label>
            <input 
              z-input
              type="number"
              [(ngModel)]="maxVolume" 
              min="0"
              max="100"
              class="w-full"
              (keydown.enter)="save()"
            />
            <p class="text-[10px] text-muted-foreground italic ml-1 leading-relaxed">
              Initial safety limit when connecting or restarting. You can still increase it later.
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
            [zDisabled]="!name"
            class="sm:px-8"
          >
            {{ data.mode === 'create' ? 'Create Source' : 'Save Changes' }}
          </z-button>
        </div>
      </div>
    </div>
  `,
  viewProviders: [provideIcons({ lucideX, lucideMusic, lucideSettings })]
})
export class SpotifySourceFormDialogComponent {
  public data = inject<SpotifySourceDialogData>(DIALOG_DATA);
  public ref = inject(DialogRef);

  name: string = this.data.source?.name || '';
  maxVolume: number = this.data.source?.maxVolume ?? 30;

  save() {
    if (this.name) {
      this.ref.close({
        name: this.name,
        maxVolume: this.maxVolume
      });
    }
  }
}
