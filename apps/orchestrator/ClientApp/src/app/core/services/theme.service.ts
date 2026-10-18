import { Injectable, signal, effect, PLATFORM_ID, inject } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';

export type Theme = 'light' | 'dark';

@Injectable({
  providedIn: 'root'
})
export class ThemeService {
  private platformId = inject(PLATFORM_ID);
  private isBrowser = isPlatformBrowser(this.platformId);
  
  private themeSignal = signal<Theme>(this.getInitialTheme());
  public theme = this.themeSignal.asReadonly();

  constructor() {
    effect(() => {
      const currentTheme = this.themeSignal();
      if (this.isBrowser) {
        this.applyTheme(currentTheme);
        localStorage.setItem('theme', currentTheme);
      }
    });
  }

  public toggleTheme(): void {
    this.themeSignal.update(t => t === 'light' ? 'dark' : 'light');
  }

  public setTheme(theme: Theme): void {
    this.themeSignal.set(theme);
  }

  private getInitialTheme(): Theme {
    if (!this.isBrowser) return 'dark';
    
    const saved = localStorage.getItem('theme') as Theme;
    if (saved) return saved;

    return window.matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark';
  }

  private applyTheme(theme: Theme): void {
    const root = window.document.documentElement;
    if (theme === 'light') {
      root.classList.add('light');
    } else {
      root.classList.remove('light');
    }
  }
}
