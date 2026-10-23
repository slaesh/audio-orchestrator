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
import { HttpErrorResponse } from '@angular/common/http';

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
  loading = signal<boolean>(true);
  loadFailed = signal<boolean>(false);
  saving = signal<boolean>(false);

  // Home Assistant MQTT Settings
  haEnabled = signal<boolean>(false);
  haHost = signal<string>('');
  haPort = signal<number>(1883);
  haUsername = signal<string>('');
  haPassword = signal<string>('');
  haHasPassword = signal<boolean>(false);
  haPasswordCleared = signal<boolean>(false);
  haUseTls = signal<boolean>(false);
  haDiscoveryPrefix = signal<string>('homeassistant');
  haBaseTopic = signal<string>('audio_orchestrator');
  haIsConnected = signal<boolean>(false);

  testingMqtt = signal<boolean>(false);
  testResult = signal<{ success: boolean; message: string } | null>(null);

  async ngOnInit() {
    await this.loadSettings();
  }

  async loadSettings() {
    this.loading.set(true);
    this.loadFailed.set(false);
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
        this.haPassword.set('');
        this.haHasPassword.set(res.homeAssistant.hasPassword);
        this.haPasswordCleared.set(false);
        this.haUseTls.set(res.homeAssistant.useTls);
        this.haDiscoveryPrefix.set(res.homeAssistant.discoveryPrefix || 'homeassistant');
        this.haBaseTopic.set(res.homeAssistant.baseTopic || 'audio_orchestrator');
        this.haIsConnected.set(res.homeAssistant.isConnected);
      }
    } catch (e) {
      console.error('Failed to load settings', e);
      this.loadFailed.set(true);
      this.toast.error('Fehler beim Laden der Einstellungen');
    } finally {
      this.loading.set(false);
    }
  }

  selectProfile(profile: VolumeProfile) {
    this.selectedProfile.set(profile);
  }

  clearPassword() {
    this.haHasPassword.set(false);
    this.haPassword.set('');
    this.haPasswordCleared.set(true);
  }

  onPasswordChange(val: string) {
    this.haPassword.set(val);
  }

  async testConnection() {
    const host = this.haHost().trim();
    if (!host) {
      this.toast.error('Bitte Broker Host / IP angeben');
      return;
    }

    const port = this.haPort() || 1883;
    if (port < 1 || port > 65535) {
      this.toast.error('Ungültiger Port (1-65535)');
      return;
    }

    this.testingMqtt.set(true);
    this.testResult.set(null);

    try {
      const isUsernameEmpty = !this.haUsername().trim();
      const enteredPassword = this.haPassword();
      const passwordToSend = isUsernameEmpty
        ? undefined
        : (enteredPassword ? enteredPassword : (this.haPasswordCleared() ? '' : undefined));
      const res = await this.data.testMqttConnection({
        host,
        port: this.haPort() || 1883,
        username: isUsernameEmpty ? undefined : this.haUsername().trim(),
        password: passwordToSend,
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
    if (this.loading() || this.loadFailed()) {
      return;
    }

    if (this.haEnabled()) {
      if (!this.haHost().trim()) {
        this.toast.error('Bitte Broker Host / IP angeben');
        return;
      }

      const port = this.haPort() || 1883;
      if (port < 1 || port > 65535) {
        this.toast.error('Ungültiger Port (1-65535)');
        return;
      }

      const discPrefix = this.haDiscoveryPrefix().trim().replace(/\/+$/, '');
      const baseTopic = this.haBaseTopic().trim().replace(/\/+$/, '');

      if (!discPrefix || discPrefix.includes('+') || discPrefix.includes('#') || /\s/.test(discPrefix)) {
        this.toast.error('Ungültiger Discovery-Präfix (+, # und Leerzeichen sind nicht erlaubt)');
        return;
      }

      if (!baseTopic || baseTopic.includes('+') || baseTopic.includes('#') || /\s/.test(baseTopic)) {
        this.toast.error('Ungültiges Basis-Topic (+, # und Leerzeichen sind nicht erlaubt)');
        return;
      }
    }

    this.saving.set(true);
    try {
      this.data.setShowRawVolume(this.showRaw());

      const isUsernameEmpty = !this.haUsername().trim();
      const enteredPassword = this.haPassword();
      const passwordToSend = isUsernameEmpty
        ? ''
        : (enteredPassword ? enteredPassword : (this.haPasswordCleared() ? '' : undefined));
      const haSettings: HomeAssistantSettingsDto = {
        enabled: this.haEnabled(),
        host: this.haHost().trim(),
        port: this.haPort() || 1883,
        username: isUsernameEmpty ? undefined : this.haUsername().trim(),
        password: passwordToSend,
        hasPassword: isUsernameEmpty ? false : this.haHasPassword(),
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
    } catch (e: unknown) {
      const msg = e instanceof HttpErrorResponse && typeof e.error === 'string'
        ? e.error
        : 'Fehler beim Speichern der Einstellungen';
      this.toast.error(msg);
    } finally {
      this.saving.set(false);
    }
  }

  close() {
    this.ref.close();
  }
}
