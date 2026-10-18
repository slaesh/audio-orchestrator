import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';

@Component({
  selector: 'app-widget-layout',
  standalone: true,
  imports: [RouterOutlet],
  template: `
    <div class="min-h-screen bg-background text-foreground flex flex-col items-center justify-center p-6 sm:p-12 overflow-hidden">
      <!-- Animated Background Blobs -->
      <div class="fixed inset-0 overflow-hidden -z-10 pointer-events-none">
        <div class="absolute -top-[10%] -left-[10%] w-[40%] h-[40%] bg-primary/10 rounded-full blur-[120px] animate-pulse"></div>
        <div class="absolute -bottom-[10%] -right-[10%] w-[40%] h-[40%] bg-primary/10 rounded-full blur-[120px] animate-pulse" style="animation-delay: 1s"></div>
      </div>

      <main class="w-full max-w-4xl animate-in fade-in duration-1000">
        <router-outlet />
      </main>
    </div>
  `,
  styles: [`
    :host {
      display: block;
      width: 100%;
      height: 100%;
    }
  `]
})
export class WidgetLayoutComponent {}
