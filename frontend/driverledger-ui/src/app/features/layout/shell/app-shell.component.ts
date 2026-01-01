import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { SidebarComponent } from '../sidebar/sidebar.component';
import { AiDrawerComponent } from '../ai-drawer/ai-drawer.component';
import { TopbarComponent } from '../topbar/topbar.component';

@Component({
  selector: 'dl-app-shell',
  standalone: true,
  imports: [RouterOutlet, SidebarComponent, AiDrawerComponent, TopbarComponent],
  templateUrl: './app-shell.component.html',
  styleUrl: './app-shell.component.css',
})
export class AppShellComponent {
  aiOpen = false;

  openAi() { this.aiOpen = true; }
  closeAi() { this.aiOpen = false; }
}
