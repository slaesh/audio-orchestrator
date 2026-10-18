import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { DataService } from '../../../../core/services/data.service';
import { GroupCardComponent } from '../group-card/group-card.component';
import { Dialog } from '@angular/cdk/dialog';
import { GroupFormDialogComponent } from '../../../../shared/dialogs/group-form-dialog.component';
import { firstValueFrom } from 'rxjs';
import { ZardButtonComponent } from '../../../../core/ui/button/button.component';
import { NgIcon, provideIcons } from '@ng-icons/core';
import { lucidePlus, lucideLayers } from '@ng-icons/lucide';

@Component({
  selector: 'app-group-list',
  standalone: true,
  imports: [CommonModule, GroupCardComponent, ZardButtonComponent, NgIcon],
  templateUrl: './group-list.component.html',
  styleUrl: './group-list.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
  viewProviders: [provideIcons({ lucidePlus, lucideLayers })],
})
export class GroupListComponent {
  public data = inject(DataService);
  private dialog = inject(Dialog);

  async onCreateGroup() {
    const dialogRef = this.dialog.open<any>(GroupFormDialogComponent, {
      data: { title: 'Create Group', mode: 'create' },
    });

    const result = (await firstValueFrom(dialogRef.closed)) as
      | { name: string }
      | undefined;
    if (result) {
      this.data.createGroup(result.name);
    }
  }

  async onEditGroup(id: string) {
    const group = this.data.groups()?.find((g) => g.id === id);
    const dialogRef = this.dialog.open<any>(GroupFormDialogComponent, {
      data: {
        title: 'Update Group',
        mode: 'edit',
        name: group?.name || '',
      },
    });

    const result = (await firstValueFrom(dialogRef.closed)) as
      | { name: string }
      | undefined;
    if (result) {
      this.data.renameGroup(id, result.name);
    }
  }
}
