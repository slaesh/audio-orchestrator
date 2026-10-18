import { ChangeDetectionStrategy, Component, input, output, computed, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { DataService } from '../../../../core/services/data.service';
import { ZardComboboxComponent, ZardComboboxOption, ZardComboboxGroup } from '../../../../core/ui/combobox/combobox.component';
import { provideIcons } from '@ng-icons/core';
import { lucidePlay, lucidePause, lucideVolumeX } from '@ng-icons/lucide';
import { GroupDto, StreamDto } from '@api';

@Component({
  selector: 'app-stream-selector',
  standalone: true,
  imports: [CommonModule, FormsModule, ZardComboboxComponent],
  templateUrl: './stream-selector.component.html',
  styleUrl: './stream-selector.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
  viewProviders: [provideIcons({ lucidePlay, lucidePause, lucideVolumeX })]
})
export class StreamSelectorComponent {
  public readonly data = inject(DataService);

  readonly currentStreamId = input<string | undefined>();
  readonly groupId = input.required<string>();
  
  readonly onSelect = output<string>();

  protected readonly sourceGroups = computed<ZardComboboxGroup[]>(() => {
    // 1. Radio Stations
    const radioOptions: ZardComboboxOption[] = this.data.streams().map((s) => ({
      value: s.id,
      label: s.name,
      icon: s.status === 'playing' ? 'lucidePlay' : 'lucidePause',
    }));

    // 2. Spotify Sources (Independent Connect Instances)
    const spotifyOptions: ZardComboboxOption[] = this.data.spotifySources().map(s => {
      const streamId = `spotify_${s.id}`;
      return {
        value: streamId,
        label: `Spotify: ${s.name}`,
        icon: s.isActive ? 'lucidePlay' : 'lucidePause'
      };
    });

    const options: ZardComboboxGroup[] = [];

    // Always add a "No Source" option
    options.push({
      label: 'System',
      options: [{ value: 'default', label: 'No Source (Silence)', icon: 'lucideVolumeX' }]
    });

    if (spotifyOptions.length > 0) {
      options.push({ label: 'Spotify Connect', options: spotifyOptions });
    }

    if (radioOptions.length > 0) {
      options.push({ label: 'Radio Stations', options: radioOptions });
    }

    return options;
  });

  handleStreamChange(streamId: string | null) {
    if (streamId) {
      this.onSelect.emit(streamId);
    }
  }
}
