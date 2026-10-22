import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
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
  lucideRadio,
  lucideCircleCheck,
  lucideCircleAlert,
  lucideLoaderCircle,
} from '@ng-icons/lucide';
import { ZardButtonComponent } from '../../../core/ui/button/button.component';
import { DataService } from '../../../core/services/data.service';
import { ToastService } from '../../../core/services/toast.service';
import { VolumeProfile, HomeAssistantSettingsDto } from '@api';

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
      lucideRadio,
      lucideCircleCheck,
      lucideCircleAlert,
      lucideLoaderCircle,
    }),
  ],
})
export class SettingsDialogComponent implements OnInit {
  public data = inject(DataService);
  public toast = inject(ToastService);
  public ref = inject(DialogRef);

  selectedProfile = signal<VolumeProfile>(this.data.volumeProfile());
  showRaw = signal<boolean>(this.data.showRawVolume());
  saving = signal<boolean>(false);

  // Home Assistant MQTT Settings
  haEnabled = signal<boolean>(false);
  haHost = signal<string>('');
  haPort = signal<number>(1883);
  haUsername = signal<string>('');
  haPassword = signal<string>('');
  haUseTls = signal<boolean>(false);
  haDiscoveryPrefix = signal<string>('homeassistant');
  haBaseTopic = signal<string>('audio_orchestrator');
  haIsConnected = signal<boolean>(false);

  testingMqtt = signal<boolean>(false);
  testResult = signal<{ success: boolean; message: string } | null>(null);

  async ngOnInit() {
    try {
      const res = await this.data.getSettings();
      if (res.volumeProfile) {
        this.selectedProfile.set(res.volumeProfile);
      }
      if (res.homeAssistant) {
        this.haEnabled.set(res.homeAssistant.enabled);
        this.haHost.set(res.homeAssistant.host ?? '');
        this.haPort.set(res.homeAssistant.port || 1883);
        this.haUsername.set(res.homeAssistant.username ?? '');
        this.haPassword.set(res.homeAssistant.password ?? '');
        this.haUseTls.set(res.homeAssistant.useTls);
        this.haDiscoveryPrefix.set(res.homeAssistant.discoveryPrefix || 'homeassistant');
        this.haBaseTopic.set(res.homeAssistant.baseTopic || 'audio_orchestrator');
        this.haIsConnected.set(res.homeAssistant.isConnected);
      }
    } catch (e) {
      console.error('Failed to load settings', e);
    }
  }

  selectProfile(profile: VolumeProfile) {
    this.selectedProfile.set(profile);
  }

  async testConnection() {
    const host = this.haHost().trim();
    if (!host) {
      this.toast.error('Bitte Broker Host / IP angeben');
      return;
    }

    this.testingMqtt.set(true);
    this.testResult.set(null);

    try {
      const res = await this.data.testMqttConnection({
        host,
        port: this.haPort() || 1883,
        username: this.haUsername().trim() || undefined,
        password: this.haPassword() || undefined,
        useTls: this.haUseTls(),
      });

      this.testResult.set({ success: res.success, message: res.message });
      if (res.success) {
        this.toast.success(res.message);
        return;
      }
      this.toast.error(res.message);
    } catch (e: unknown) {
      const err = e instanceof Error ? e.message : 'Verbindung fehlgeschlagen';
      this.testResult.set({ success: false, message: err });
      this.toast.error(err);
    } finally {
      this.testingMqtt.set(false);
    }
  }

  async saveAndClose() {
    this.saving.set(true);
    try {
      this.data.setShowRawVolume(this.showRaw());

      const haSettings: HomeAssistantSettingsDto = {
        enabled: this.haEnabled(),
        host: this.haHost().trim(),
        port: this.haPort() || 1883,
        username: this.haUsername().trim() || undefined,
        password: this.haPassword() || undefined,
        useTls: this.haUseTls(),
        discoveryPrefix: this.haDiscoveryPrefix().trim() || 'homeassistant',
        baseTopic: this.haBaseTopic().trim() || 'audio_orchestrator',
        isConnected: this.haIsConnected(),
      };

      await this.data.updateSettings({
        volumeProfile: this.selectedProfile(),
        homeAssistant: haSettings,
      });

      this.toast.success('Einstellungen erfolgreich gespeichert');
      this.ref.close();
    } catch (e) {
      this.toast.error('Fehler beim Speichern der Einstellungen');
    } finally {
      this.saving.set(false);
    }
  }

  close() {
    this.ref.close();
  }
}
