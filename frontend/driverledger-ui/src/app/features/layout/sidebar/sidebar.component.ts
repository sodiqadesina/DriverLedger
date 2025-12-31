import { Component } from '@angular/core';
import { RouterModule } from '@angular/router';

@Component({
  selector: 'dl-sidebar',
  standalone: true,
  imports: [RouterModule],
  template: `
    <aside class="hidden lg:flex lg:flex-col w-64 border-r min-h-screen p-4">
      <div class="font-bold text-xl mb-6">DriverLedger</div>

      <nav class="flex flex-col gap-2">
        <a routerLink="/app/dashboard" routerLinkActive="font-semibold">Dashboard</a>
        <a routerLink="/app/receipts" routerLinkActive="font-semibold">Receipts</a>
        <a routerLink="/app/ledger" routerLinkActive="font-semibold">Ledger</a>
        <a routerLink="/app/live-statement" routerLinkActive="font-semibold">Live Statement</a>
      </nav>
    </aside>
  `
})
export class SidebarComponent {}
