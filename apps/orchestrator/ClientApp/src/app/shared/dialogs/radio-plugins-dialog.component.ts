import { Component, inject, signal, ChangeDetectionStrategy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Dialog, DialogRef } from '@angular/cdk/dialog';
import { NgIcon, provideIcons } from '@ng-icons/core';
import {
  lucideX,
  lucideBoxes,
  lucidePlus,
  lucideTrash2,
  lucideSettings,
  lucideCheck,
  lucidePower,
  lucideCircleAlert,
  lucideCircleArrowUp
} from '@ng-icons/lucide';
import { ZardButtonComponent } from '@ui/button/button.component';
import { ZardBadgeComponent } from '@ui/badge/badge.component';
import { ZardInputDirective } from '@ui/input/input.directive';
import { ConfirmService } from '@ui/dialog/confirm.service';
import { DataService } from '../../core/services/data.service';
import { RadioPluginDto, RadioPluginCreateRequest, RadioPluginUpdateRequest } from '@api';
import { NodeUpgradeDialogComponent, NodeUpgradeDialogData } from './node-upgrade-dialog/node-upgrade-dialog.component';

@Component({
  selector: 'app-radio-plugins-dialog',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    CommonModule,
    FormsModule,
    NgIcon,
    ZardButtonComponent,
    ZardBadgeComponent,
    ZardInputDirective
  ],
  template: `
    <div class="fixed inset-0 z-50 flex items-center justify-center p-4 bg-background/80 backdrop-blur-sm animate-in fade-in duration-200" (click)="ref.close()">
      <div class="relative w-full max-w-[650px] max-h-[85vh] flex flex-col overflow-hidden rounded-2xl border border-border bg-card p-6 shadow-2xl animate-in zoom-in-95 duration-200" (click)="$event.stopPropagation()">
        
        <!-- Header -->
        <div class="flex items-center justify-between pb-4 border-b border-border/40">
          <div class="flex items-center gap-3">
            <div class="size-10 rounded-xl bg-primary/10 text-primary flex items-center justify-center">
              <ng-icon name="lucideBoxes" size="1.25rem" />
            </div>
            <div>
              <h2 class="text-xl font-bold tracking-tight">
                {{ isEditing() ? (editMode() === 'create' ? 'Register New Radio Plugin' : 'Edit Radio Plugin') : 'Radio Plugins' }}
              </h2>
            </div>
          </div>
          <button (click)="isEditing() ? cancelEdit() : ref.close()" class="p-1.5 rounded-lg hover:bg-muted transition-colors opacity-60 hover:opacity-100 cursor-pointer">
            <ng-icon name="lucideX" />
          </button>
        </div>

        <!-- Content Area -->
        <div class="overflow-y-auto py-4 space-y-4 flex-1 pr-1">
          
          <!-- Mode: List Plugins -->
          @if (!isEditing()) {
            <div class="flex items-center justify-between mb-2">
              <span class="text-xs font-semibold text-muted-foreground uppercase tracking-wider">
                Configured Handlers ({{ data.radioPlugins().length }})
              </span>
              <z-button zType="outline" zSize="xs" (click)="startCreate()">
                <ng-icon name="lucidePlus" class="mr-1" />
                Add Plugin
              </z-button>
            </div>

            <div class="space-y-3">
              @for (plugin of data.radioPlugins(); track plugin.id) {
                <div class="rounded-xl border border-border/60 bg-muted/20 p-4 transition-all duration-200 hover:border-primary/40 space-y-3">
                  <div class="flex items-start justify-between gap-3">
                    <div class="space-y-1">
                      <div class="flex items-center gap-2">
                        <span class="font-bold text-sm tracking-tight">{{ plugin.name }}</span>
                        <z-badge 
                          [zType]="plugin.enabled ? 'default' : 'secondary'"
                          class="text-[9px] py-0 px-1.5 font-mono uppercase"
                        >
                          {{ plugin.enabled ? 'Active' : 'Disabled' }}
                        </z-badge>
                        <z-badge zType="secondary" class="text-[9px] py-0 px-1.5 opacity-60 font-mono" title="Plugin Version">
                          v{{ plugin.currentVersion || '0.1.0' }}
                        </z-badge>
                        @if (plugin.hasUpdate) {
                          <button 
                            type="button" 
                            (click)="openUpgradeDialog(plugin)" 
                            class="flex items-center gap-1 px-2 py-0.5 rounded-full bg-amber-500/15 text-amber-400 hover:bg-amber-500/25 border border-amber-500/30 text-[10px] font-mono transition-colors cursor-pointer"
                            [title]="'Update available to v' + plugin.latestCompatibleVersion"
                          >
                            <ng-icon name="lucideCircleArrowUp" size="0.75rem" />
                            <span>Update v{{ plugin.latestCompatibleVersion }}</span>
                          </button>
                        }
                      </div>
                      <p class="text-[11px] font-mono text-muted-foreground break-all">
                        Image: <span class="text-foreground/80">{{ plugin.dockerImage }}</span>
                        @if (plugin.gitHubRepository) {
                          <span class="ml-2 text-muted-foreground/60">(repo: {{ plugin.gitHubRepository }})</span>
                        }
                      </p>
                    </div>

                    <div class="flex items-center gap-1">
                      @if (plugin.hasUpdate) {
                        <button 
                          (click)="openUpgradeDialog(plugin)"
                          class="p-1.5 rounded-lg border border-amber-500/40 bg-amber-500/10 hover:bg-amber-500/20 text-amber-400 transition-all cursor-pointer"
                          [title]="'Update Available (v' + plugin.latestCompatibleVersion + ')'"
                        >
                          <ng-icon name="lucideCircleArrowUp" size="0.9rem" />
                        </button>
                      }
                      <button 
                        (click)="data.toggleRadioPlugin(plugin)"
                        class="p-1.5 rounded-lg border border-border/40 hover:bg-muted text-muted-foreground hover:text-foreground transition-all"
                        [title]="plugin.enabled ? 'Disable Plugin' : 'Enable Plugin'"
                      >
                        <ng-icon name="lucidePower" [class.text-primary]="plugin.enabled" size="0.9rem" />
                      </button>
                      <button 
                        (click)="startEdit(plugin)"
                        class="p-1.5 rounded-lg border border-border/40 hover:bg-muted text-muted-foreground hover:text-foreground transition-all"
                        title="Edit Plugin"
                      >
                        <ng-icon name="lucideSettings" size="0.9rem" />
                      </button>
                      <button 
                        (click)="deletePlugin(plugin.id, plugin.name)"
                        class="p-1.5 rounded-lg border border-border/40 hover:bg-destructive/10 text-muted-foreground hover:text-destructive transition-all"
                        title="Delete Plugin"
                      >
                        <ng-icon name="lucideTrash2" size="0.9rem" />
                      </button>
                    </div>
                  </div>

                  <!-- Regex Patterns -->
                  <div class="space-y-1.5 pt-2 border-t border-border/30">
                    <span class="text-[10px] font-bold uppercase tracking-wider text-muted-foreground/70">
                      URL Regex Patterns:
                    </span>
                    <div class="flex flex-wrap gap-1.5">
                      @for (pattern of plugin.urlPatterns; track pattern) {
                        <span class="text-[10px] font-mono px-2 py-0.5 rounded-md bg-background border border-border/60 text-primary/90">
                          {{ pattern }}
                        </span>
                      } @empty {
                        <span class="text-[10px] text-muted-foreground italic">No URL patterns assigned</span>
                      }
                    </div>
                  </div>
                </div>
              } @empty {
                <div class="py-10 text-center glass-panel border-dashed border-2 text-muted-foreground/50">
                  <ng-icon name="lucideBoxes" size="2rem" class="mb-2 opacity-30" />
                  <p class="text-sm font-medium">No custom radio plugins configured</p>
                  <p class="text-xs text-muted-foreground/60 mt-1">Default FFmpeg bridge handles all streams.</p>
                </div>
              }
            </div>
          }

          <!-- Mode: Create / Edit Form -->
          @if (isEditing()) {
            <div class="space-y-4 animate-in fade-in duration-200">
              <div class="space-y-3">
                @if (editMode() === 'create') {
                  <div class="space-y-1">
                    <label class="text-[11px] uppercase font-bold tracking-wider text-muted-foreground">Plugin Identifier</label>
                    <input 
                      z-input 
                      [(ngModel)]="formId" 
                      placeholder="e.g. audalaxy, shoutcast-meta..."
                      class="w-full text-sm"
                    />
                  </div>
                }

                <div class="space-y-1">
                  <label class="text-[11px] uppercase font-bold tracking-wider text-muted-foreground">Plugin Name</label>
                  <input 
                    z-input 
                    [(ngModel)]="formName" 
                    placeholder="e.g. Audalaxy / StreamABC"
                    class="w-full text-sm"
                  />
                </div>

                <div class="space-y-1">
                  <label class="text-[11px] uppercase font-bold tracking-wider text-muted-foreground">Docker Image</label>
                  <input 
                    z-input 
                    [ngModel]="formImage"
                    (ngModelChange)="onImageChange($event)"
                    placeholder="e.g. ghcr.io/slaesh/radio-node-audalaxy:latest"
                    class="w-full text-sm font-mono"
                  />
                </div>

                <div class="space-y-1">
                  <div class="flex items-center justify-between">
                    <label class="text-[11px] uppercase font-bold tracking-wider text-muted-foreground">GitHub Repository (Optional)</label>
                    <span class="text-[10px] text-muted-foreground">For release notes & updates (owner/repo)</span>
                  </div>
                  <input 
                    z-input 
                    [(ngModel)]="formGitHubRepo" 
                    placeholder="e.g. slaesh/radio-node-audalaxy"
                    class="w-full text-sm font-mono"
                  />
                </div>

                <div class="space-y-1">
                  <div class="flex items-center justify-between">
                    <label class="text-[11px] uppercase font-bold tracking-wider text-muted-foreground">URL Regex Patterns (One per line)</label>
                    <span class="text-[10px] text-muted-foreground">Case-insensitive regex</span>
                  </div>
                  <textarea 
                    [(ngModel)]="formPatternsRaw" 
                    rows="4" 
                    placeholder="sunshine-live\.de&#10;streamabc\.net&#10;radiobob\.de"
                    class="w-full rounded-md border border-input bg-transparent px-3 py-2 text-xs font-mono shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
                  ></textarea>
                </div>

                <div class="flex items-center gap-2 pt-1">
                  <input 
                    type="checkbox" 
                    id="pluginEnabled" 
                    [(ngModel)]="formEnabled"
                    class="rounded border-input text-primary focus:ring-primary size-4"
                  />
                  <label for="pluginEnabled" class="text-xs font-medium cursor-pointer">
                    Enable this plugin for URL matching
                  </label>
                </div>
              </div>
            </div>
          }
        </div>

        <!-- Footer -->
        <div class="pt-3 border-t border-border/40 flex justify-end gap-2">
          @if (!isEditing()) {
            <z-button zType="outline" zSize="sm" (click)="ref.close()">Close</z-button>
          } @else {
            <z-button zType="outline" zSize="sm" (click)="cancelEdit()">Cancel</z-button>
            <z-button 
              zType="default" 
              zSize="sm" 
              (click)="savePlugin()"
              [zDisabled]="!formName || !formImage || (editMode() === 'create' && !formId)"
            >
              <ng-icon name="lucideCheck" class="mr-1" />
              Save Plugin
            </z-button>
          }
        </div>
      </div>
    </div>
  `,
  viewProviders: [
    provideIcons({
      lucideX,
      lucideBoxes,
      lucidePlus,
      lucideTrash2,
      lucideSettings,
      lucideCheck,
      lucidePower,
      lucideCircleAlert,
      lucideCircleArrowUp
    })
  ]
})
export class RadioPluginsDialogComponent {
  public data = inject(DataService);
  public ref = inject(DialogRef);
  private confirm = inject(ConfirmService);
  private dialog = inject(Dialog);

  isEditing = signal(false);
  editMode = signal<'create' | 'edit'>('create');
  currentEditId = signal<string>('');

  formId = '';
  formName = '';
  formImage = '';
  formGitHubRepo = '';
  formPatternsRaw = '';
  formEnabled = true;

  onImageChange(newImage: string) {
    this.formImage = newImage;
    if (!this.formGitHubRepo) {
      const autoRepo = this.extractGitHubRepo(newImage);
      if (autoRepo) {
        this.formGitHubRepo = autoRepo;
      }
    }
  }

  private extractGitHubRepo(image: string): string | null {
    if (!image) return null;
    const withoutTag = image.split(':')[0];
    const segments = withoutTag.split('/');
    if (segments.length >= 2) {
      return `${segments.at(-2)}/${segments.at(-1)}`;
    }
    return null;
  }

  startCreate() {
    this.editMode.set('create');
    this.formId = '';
    this.formName = '';
    this.formImage = 'ghcr.io/slaesh/radio-node-audalaxy:latest';
    this.formGitHubRepo = 'slaesh/radio-node-audalaxy';
    this.formPatternsRaw = '';
    this.formEnabled = true;
    this.isEditing.set(true);
  }

  startEdit(plugin: RadioPluginDto) {
    this.editMode.set('edit');
    this.currentEditId.set(plugin.id);
    this.formId = plugin.id;
    this.formName = plugin.name;
    this.formImage = plugin.dockerImage;
    this.formGitHubRepo = plugin.gitHubRepository || '';
    this.formPatternsRaw = (plugin.urlPatterns || []).join('\n');
    this.formEnabled = plugin.enabled;
    this.isEditing.set(true);
  }

  cancelEdit() {
    this.isEditing.set(false);
  }

  async savePlugin() {
    const patterns = this.formPatternsRaw
      .split('\n')
      .map(p => p.trim())
      .filter(p => p.length > 0);

    const repo = this.formGitHubRepo.trim() || undefined;

    if (this.editMode() === 'create') {
      const req: RadioPluginCreateRequest = {
        id: this.formId.trim().toLowerCase(),
        name: this.formName.trim(),
        dockerImage: this.formImage.trim(),
        urlPatterns: patterns,
        enabled: this.formEnabled,
        gitHubRepository: repo
      };
      await this.data.createRadioPlugin(req);
    } else {
      const req: RadioPluginUpdateRequest = {
        name: this.formName.trim(),
        dockerImage: this.formImage.trim(),
        urlPatterns: patterns,
        enabled: this.formEnabled,
        gitHubRepository: repo
      };
      await this.data.updateRadioPlugin(this.currentEditId(), req);
    }

    this.isEditing.set(false);
  }

  async deletePlugin(id: string, name: string) {
    const ok = await this.confirm.delete(
      'Delete Radio Plugin',
      `Are you sure you want to delete plugin "${name}"?`,
    );
    if (!ok) return;
    await this.data.deleteRadioPlugin(id);
  }

  openUpgradeDialog(plugin: RadioPluginDto): void {
    const repo = plugin.gitHubRepository || this.extractGitHubRepo(plugin.dockerImage) || undefined;

    this.dialog.open(NodeUpgradeDialogComponent, {
      data: {
        type: 'radioPlugin',
        id: plugin.id,
        name: plugin.name,
        currentVersion: plugin.currentVersion || '0.1.0',
        targetVersion: plugin.latestCompatibleVersion || 'latest',
        repository: repo,
      } satisfies NodeUpgradeDialogData,
    });
  }
}
