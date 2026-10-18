import { Injectable } from '@angular/core';
import { toast } from 'ngx-sonner';

@Injectable({
  providedIn: 'root'
})
export class ToastService {
  public success(message: string, description?: string): void {
    toast.success(message, {
      description: description,
    });
  }

  public error(message: string, description?: string): void {
    toast.error(message, {
      description: description,
    });
  }

  public info(message: string, description?: string): void {
    toast(message, {
      description: description,
    });
  }

  public warning(message: string, description?: string): void {
    toast.warning(message, {
      description: description,
    });
  }
}
