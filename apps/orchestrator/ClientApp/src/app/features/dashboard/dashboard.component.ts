import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Dialog } from '@angular/cdk/dialog';
import { DataService } from '../../core/services/data.service';
import { ThemeService } from '../../core/services/theme.service';
import { ZardButtonComponent } from '../../core/ui/button/button.component';
import { NgIcon, provideIcons } from '@ng-icons/core';
import {
  lucideSun,
  lucideMoon,
  lucideRefreshCw,
  lucideSettings,
} from '@ng-icons/lucide';
import { SettingsDialogComponent } from '../../shared/dialogs/settings-dialog/settings-dialog.component';

// Feature components
import { GroupListComponent } from '../groups/components/group-list/group-list.component';
import { UnassignedRoomsComponent } from '../rooms/components/unassigned-rooms/unassigned-rooms.component';
import { RadioListComponent } from '../radio/components/radio-list/radio-list.component';
import { SpotifyListComponent } from '../spotify/components/spotify-list/spotify-list.component';

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [
    CommonModule, 
    ZardButtonComponent, 
    NgIcon, 
    GroupListComponent, 
    UnassignedRoomsComponent, 
    RadioListComponent,
    SpotifyListComponent
  ],
  templateUrl: './dashboard.component.html',
  styleUrl: './dashboard.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
  viewProviders: [
    provideIcons({
      lucideSun,
      lucideMoon,
      lucideRefreshCw,
      lucideSettings,
    }),
  ],
})
export class DashboardComponent {
  public data = inject(DataService);
  public themeService = inject(ThemeService);
  private dialog = inject(Dialog);

  openSettings() {
    this.dialog.open(SettingsDialogComponent);
  }
}
