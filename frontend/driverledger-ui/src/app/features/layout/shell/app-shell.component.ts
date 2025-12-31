import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { SidebarComponent } from '../sidebar/sidebar.component';
import { BottomNavComponent } from '../bottom-nav/bottom-nav.component';
import { AiDrawerComponent } from '../ai-drawer/ai-drawer.component';

@Component({
  selector: 'dl-app-shell',
  standalone: true,
  imports: [RouterOutlet, SidebarComponent, BottomNavComponent, AiDrawerComponent],
  template: `
    <div class="min-h-screen flex">
      <dl-sidebar />

      <main class="flex-1 p-4 pb-20 lg:pb-4">
        <div class="flex justify-end lg:hidden mb-2">
          <button class="underline text-sm" (click)="aiOpen=true">AI</button>
        </div>

        <router-outlet />
      </main>

      <dl-bottom-nav (ai)="aiOpen=true" />
      <dl-ai-drawer [open]="aiOpen" (close)="aiOpen=false" />
    </div>
  `
})
export class AppShellComponent {
  aiOpen = false;
}
