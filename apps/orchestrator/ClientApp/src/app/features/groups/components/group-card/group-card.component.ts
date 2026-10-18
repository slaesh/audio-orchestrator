import {
  ChangeDetectionStrategy,
  Component,
  input,
  output,
  inject,
} from '@angular/core';
import { Dialog } from '@angular/cdk/dialog';
import { firstValueFrom } from 'rxjs';
import { RenameDialogComponent } from '../../../../shared/dialogs/rename-dialog.component';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { DragDropModule, CdkDragDrop } from '@angular/cdk/drag-drop';
import { DataService } from '../../../../core/services/data.service';
import { ConfirmService } from '../../../../core/ui/dialog/confirm.service';
import { GroupDto, RoomDto } from '@api';
import { ZardCardComponent } from '../../../../core/ui/card/card.component';
import { ZardButtonComponent } from '../../../../core/ui/button/button.component';
import { ZardSliderComponent } from '../../../../core/ui/slider/slider.component';
import { RoomItemComponent } from '../../../rooms/components/room-item/room-item.component';
import { StreamSelectorComponent } from '../stream-selector/stream-selector.component';
import { NgIcon, provideIcons } from '@ng-icons/core';
import {
  lucideVolume2,
  lucideVolumeX,
  lucideTrash2,
  lucideSettings,
  lucideMusic,
  lucideLayers,
} from '@ng-icons/lucide';

@Component({
  selector: 'app-group-card',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    DragDropModule,
    ZardCardComponent,
    ZardButtonComponent,
    ZardSliderComponent,
    RoomItemComponent,
    StreamSelectorComponent,
    NgIcon,
  ],
  templateUrl: './group-card.component.html',
  styleUrl: './group-card.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
  viewProviders: [
    provideIcons({
      lucideVolume2,
      lucideVolumeX,
      lucideTrash2,
      lucideSettings,
      lucideMusic,
      lucideLayers,
    }),
  ],
})
export class GroupCardComponent {
  public readonly data = inject(DataService);
  private readonly confirm = inject(ConfirmService);
  private readonly dialog = inject(Dialog);

  readonly group = input.required<GroupDto>();

  readonly onDelete = output<string>();
  readonly onRename = output<string>();
  readonly onSourceChange = output<{ groupId: string; streamId: string }>();
  readonly onRoomMove = output<{ roomId: string; groupId: string }>();
  readonly onUnassignRoom = output<string>();

  async handleDelete() {
    const ok = await this.confirm.delete(
      'Delete Group',
      `Are you sure you want to delete "${this.group().name || 'this group'}"? All assigned rooms will be disconnected.`,
      'delete',
    );
    if (ok) {
      this.onDelete.emit(this.group().id);
    }
  }

  handleStreamChange(streamId: string) {
    this.onSourceChange.emit({ groupId: this.group().id, streamId });
  }

  handleGroupVolumeChange(percent: number) {
    this.data.setGroupVolume(this.group().id, percent, this.group().isMuted);
  }

  handleRoomVolumeChange(
    roomId: string,
    event: { percent: number; muted: boolean },
  ) {
    this.data.setRoomVolume(roomId, event.percent, event.muted);
  }

  handleRoomMixChange(roomId: string, percent: number) {
    this.data.setRoomMix(roomId, percent);
  }

  handleDrop(event: CdkDragDrop<string>) {
    if (event.previousContainer === event.container) return;

    // The roomId was passed as cdkDragData in the UnassignedRoomsComponent
    const roomId = event.item.data;
    const groupId = this.group().id;

    this.onRoomMove.emit({ roomId, groupId });
  }

  toggleGroupMute() {
    this.data.setGroupVolume(
      this.group().id,
      this.group().masterVolume,
      !this.group().isMuted,
    );
  }

  async onRoomRename(roomId: string) {
    const room = this.group().rooms.find((r) => r.id === roomId);
    if (!room) return;

    const dialogRef = this.dialog.open<string>(RenameDialogComponent, {
      data: {
        title: 'Rename Room',
        label: 'Friendly Name',
        initialValue: room.friendlyName || room.name,
        placeholder: 'e.g. Kitchen, Bedroom...',
      },
    });

    const result = await firstValueFrom(dialogRef.closed);
    if (result) {
      this.data.renameRoom(roomId, result);
    }
  }
}
