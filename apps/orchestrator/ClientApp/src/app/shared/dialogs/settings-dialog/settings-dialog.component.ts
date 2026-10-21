import {
  ChangeDetectionStrategy,
  Component,
  inject,
  signal,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { DialogRef } from '@angular/cdk/dialog';
import { NgIcon, provideIcons } from '@ng-icons/core';
import {
  lucideSettings,
  lucideX,
  lucideCheck,
  lucideSlidersHorizontal,
  lucideActivity,
  lucideSparkles,
} from '@ng-icons/lucide';
import { ZardButtonComponent } from '../../../core/ui/button/button.component';
import { DataService } from '../../../core/services/data.service';
import { VolumeProfile } from '../../../core/utils/volume-calculator';

@Component({
  selector: 'app-settings-dialog',
  standalone: true,
  imports: [CommonModule, FormsModule, NgIcon, ZardButtonComponent],
  templateUrl: './settings-dialog.component.html',
  styleUrl: './settings-dialog.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  viewProviders: [
    provideIcons({
      lucideSettings,
      lucideX,
      lucideCheck,
      lucideSlidersHorizontal,
      lucideActivity,
      lucideSparkles,
    }),
  ],
})
export class SettingsDialogComponent {
  public data = inject(DataService);
  public ref = inject(DialogRef);

  selectedProfile = signal<VolumeProfile>(this.data.volumeProfile());
  showRaw = signal<boolean>(this.data.showRawVolume());
  saving = signal<boolean>(false);

  selectProfile(profile: VolumeProfile) {
    this.selectedProfile.set(profile);
  }

  toggleShowRaw() {
    this.showRaw.update((v) => !v);
  }

  async saveAndClose() {
    this.saving.set(true);
    try {
      this.data.setShowRawVolume(this.showRaw());
      if (this.selectedProfile() !== this.data.volumeProfile()) {
        await this.data.setVolumeProfile(this.selectedProfile());
      }
      this.ref.close();
    } finally {
      this.saving.set(false);
    }
  }

  close() {
    this.ref.close();
  }
}
