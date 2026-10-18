import { Injectable, inject } from '@angular/core';
import { Dialog, DialogConfig } from '@angular/cdk/dialog';
import { ConfirmDialogComponent, ConfirmOptions } from './confirm-dialog.component';
import { firstValueFrom } from 'rxjs';

@Injectable({ providedIn: 'root' })
export class ConfirmService {
  private dialog = inject(Dialog);

  async confirm(options: ConfirmOptions): Promise<boolean> {
    const dialogRef = this.dialog.open<boolean>(ConfirmDialogComponent, {
      data: options,
      disableClose: true,
      backdropClass: 'cdk-overlay-transparent-backdrop' // We handle backdrop in component
    });

    const result = await firstValueFrom(dialogRef.closed);
    return !!result;
  }

  async delete(title: string, message: string, confirmText?: string): Promise<boolean> {
    return this.confirm({
      title,
      message,
      confirmLabel: 'Delete',
      confirmText,
      variant: 'destructive'
    });
  }
}
