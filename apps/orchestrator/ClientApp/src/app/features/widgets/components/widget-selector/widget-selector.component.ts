import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { DataService } from '../../../../core/services/data.service';
import { RouterLink } from '@angular/router';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideLayoutGrid, lucideChevronRight, lucideSpeaker } from '@ng-icons/lucide';

@Component({
  selector: 'app-widget-selector',
  standalone: true,
  imports: [CommonModule, RouterLink, NgIcon],
  template: `
    <div class="space-y-8 max-w-2xl mx-auto">
      <div class="text-center space-y-2">
        <h1 class="text-4xl font-black tracking-tight flex items-center justify-center gap-3">
          <ng-icon name="lucideLayoutGrid" class="text-primary" />
          Select a Group
        </h1>
        <p class="text-muted-foreground text-lg">Pick a room group to control in kiosk mode</p>
      </div>

      <div class="grid grid-cols-1 gap-4">
        @for (group of data.groups(); track group.id) {
          <a [routerLink]="['/widgets/group', group.id]" 
             class="glass-panel p-6 flex items-center justify-between group hover:bg-primary/5 hover:border-primary/30 transition-all duration-300">
            <div class="flex items-center gap-6">
              <div class="size-16 rounded-2xl bg-primary/10 flex items-center justify-center text-primary group-hover:scale-110 transition-transform">
                <ng-icon name="lucideSpeaker" size="2rem" />
              </div>
              <div>
                <h3 class="text-2xl font-bold tracking-tight">{{ group.name }}</h3>
                <p class="text-muted-foreground">{{ group.rooms.length }} rooms connected</p>
              </div>
            </div>
            <ng-icon name="lucideChevronRight" size="2rem" class="text-muted-foreground/30 group-hover:text-primary group-hover:translate-x-2 transition-all" />
          </a>
        }
      </div>
    </div>
  `,
  viewProviders: [provideIcons({ lucideLayoutGrid, lucideChevronRight, lucideSpeaker })]
})
export class WidgetSelectorComponent {
  public data = inject(DataService);
}
