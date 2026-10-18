import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { ZardToastComponent } from './core/ui/toast/toast.component';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet, ZardToastComponent],
  templateUrl: './app.component.html',
  styleUrl: './app.component.css'
})
export class AppComponent {
  // AppComponent is now just a container for the main layout and global UI providers.
}
