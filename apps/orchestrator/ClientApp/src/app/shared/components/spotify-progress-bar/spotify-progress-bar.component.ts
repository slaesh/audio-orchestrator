import { ChangeDetectionStrategy, Component, input, signal, computed, effect, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { SpotifySourceDto } from '@api';

@Component({
  selector: 'app-spotify-progress-bar',
  standalone: true,
  imports: [CommonModule],
  template: `
    <div class="space-y-2">
      <div class="h-2 w-full bg-white/5 rounded-full overflow-hidden">
        <div class="h-full bg-primary transition-all duration-500 ease-linear shadow-primary/50"
             [style.width.%]="progress()"
             [class.!transition-none]="isJumpingBackward() || progress() < 1">
        </div>
      </div>
      @if (showTime()) {
        <div class="flex justify-between text-[10px] font-bold font-mono text-muted-foreground/40 uppercase tracking-widest">
          <span>{{ formatTime(displayPosition()) }}</span>
          <span>{{ formatTime(source().currentDurationMs) }}</span>
        </div>
      }
    </div>
  `,
  styles: [],
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class SpotifyProgressBarComponent implements OnDestroy {
  source = input.required<SpotifySourceDto>();
  showTime = input(false);

  private clock = signal(Date.now());
  private lastProgress = 0;
  public isJumpingBackward = signal(false);
  private jumpResetTimeout: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    effect((onCleanup) => {
      const s = this.source();
      if (!s.isActive) return;

      const id = setInterval(() => this.clock.set(Date.now()), 500);
      onCleanup(() => clearInterval(id));
    });

    effect(() => {
      const currentProgress = this.progress();
      if (currentProgress < this.lastProgress - 0.5) {
        this.isJumpingBackward.set(true);
        if (this.jumpResetTimeout) clearTimeout(this.jumpResetTimeout);
        this.jumpResetTimeout = setTimeout(() => this.isJumpingBackward.set(false), 100);
      }
      this.lastProgress = currentProgress;
    });
  }

  public displayPosition = computed(() => {
    const s = this.source();
    if (!s || s.currentPositionMs === null || s.currentPositionMs === undefined) return 0;
    if (!s.isActive || !s.lastUpdateTimestamp) return s.currentPositionMs;
    
    const elapsed = Math.max(0, this.clock() - s.lastUpdateTimestamp);
    const pos = (s.currentPositionMs || 0) + elapsed;
    const dur = s.currentDurationMs || 0;
    return dur > 0 ? Math.min(pos, dur) : pos;
  });

  public progress = computed(() => {
    const dur = this.source().currentDurationMs || 0;
    if (dur <= 0) return 0;
    const pos = this.displayPosition();
    return Math.min(100, Math.max(0, (pos / dur) * 100));
  });

  public formatTime(ms: number | null | undefined): string {
    if (!ms || ms < 0) return '0:00';
    const totalSeconds = Math.floor(ms / 1000);
    const minutes = Math.floor(totalSeconds / 60);
    const seconds = totalSeconds % 60;
    return `${minutes}:${seconds < 10 ? '0' : ''}${seconds}`;
  }

  ngOnDestroy() {
    if (this.jumpResetTimeout) {
      clearTimeout(this.jumpResetTimeout);
    }
  }
}
