import {
  ChangeDetectionStrategy,
  Component,
  inject,
  input,
  output,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { RoomDto } from '@api';
import { DataService } from '../../../../core/services/data.service';
import { ZardButtonComponent } from '../../../../core/ui/button/button.component';
import { ZardSliderComponent } from '../../../../core/ui/slider/slider.component';
import { NgIcon, provideIcons } from '@ng-icons/core';
import {
  lucideVolume2,
  lucideVolumeX,
  lucideLayers,
  lucideSettings,
  lucideTrash2,
} from '@ng-icons/lucide';
import { DragDropModule } from '@angular/cdk/drag-drop';

@Component({
  selector: 'app-room-item',
  standalone: true,
  imports: [
    CommonModule,
    ZardButtonComponent,
    ZardSliderComponent,
    NgIcon,
    DragDropModule,
  ],
  templateUrl: './room-item.component.html',
  styleUrl: './room-item.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
  viewProviders: [
    provideIcons({
      lucideVolume2,
      lucideVolumeX,
      lucideLayers,
      lucideSettings,
      lucideTrash2,
    }),
  ],
})
export class RoomItemComponent {
  public data = inject(DataService);

  readonly room = input.required<RoomDto>();
  readonly showMix = input<boolean>(true);
  readonly allowDelete = input<boolean>(false);

  readonly onUnassign = output<string>();
  readonly onDelete = output<string>();
  readonly onRename = output<string>();
  readonly onVolumeChange = output<{ percent: number; muted: boolean }>();
  readonly onMixChange = output<number>();

  toggleMute() {
    this.onVolumeChange.emit({
      percent: this.room().mixVolume,
      muted: !this.room().volumeMuted,
    });
  }

  handleVolumeChange(percent: number) {
    this.onVolumeChange.emit({
      percent,
      muted: this.room().volumeMuted,
    });
  }

  handleMixChange(percent: number) {
    this.onMixChange.emit(percent);
  }
}
