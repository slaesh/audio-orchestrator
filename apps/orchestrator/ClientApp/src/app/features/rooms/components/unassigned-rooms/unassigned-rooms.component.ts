import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { DataService } from '../../../../core/services/data.service';
import { DragDropModule, CdkDragDrop } from '@angular/cdk/drag-drop';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucideMonitor, lucideMousePointer2, lucideInfo } from '@ng-icons/lucide';
import { Dialog } from '@angular/cdk/dialog';
import { firstValueFrom } from 'rxjs';
import { RenameDialogComponent } from '../../../../shared/dialogs/rename-dialog.component';
import { RoomItemComponent } from '../room-item/room-item.component';
import { ConfirmService } from '../../../../core/ui/dialog/confirm.service';
import { RoomDto } from '@api';

@Component({
  selector: 'app-unassigned-rooms',
  standalone: true,
  imports: [CommonModule, DragDropModule, NgIcon, RoomItemComponent],
  templateUrl: './unassigned-rooms.component.html',
  styleUrl: './unassigned-rooms.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
  viewProviders: [provideIcons({ lucideMonitor, lucideMousePointer2, lucideInfo })]
})
export class UnassignedRoomsComponent {
  public data = inject(DataService);
  private dialog = inject(Dialog);
  private confirm = inject(ConfirmService);

  handleDrop(event: CdkDragDrop<RoomDto[], RoomDto[], string>) {
    if (event.previousContainer === event.container) {
       // Sorting within inventory (not implemented/required)
       return;
    }
    
    // Move from group back to inventory
    const roomId = event.item.data;
    this.data.unassignRoom(roomId);
  }

  async onRoomDelete(roomId: string) {
    const room = this.data.unassignedRooms().find(r => r.id === roomId);
    if (!room) return;

    const ok = await this.confirm.delete(
      'Remove Device',
      `Are you sure you want to remove "${room.friendlyName || room.name}"? If the device reconnects later, it will automatically reappear.`
    );
    if (!ok) return;

    await this.data.deleteRoom(roomId);
  }

  async onRoomRename(roomId: string) {
    const room = this.data.unassignedRooms().find(r => r.id === roomId);
    if (!room) return;

    const dialogRef = this.dialog.open<string>(RenameDialogComponent, {
      data: {
        title: 'Rename Room',
        label: 'Friendly Name',
        initialValue: room.friendlyName || room.name,
        placeholder: 'e.g. Kitchen, Bedroom...'
      }
    });

    const result = await firstValueFrom(dialogRef.closed);
    if (result) {
      this.data.renameRoom(roomId, result);
    }
  }
}
