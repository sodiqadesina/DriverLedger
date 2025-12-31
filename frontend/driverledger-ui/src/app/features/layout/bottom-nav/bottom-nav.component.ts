import { Component, EventEmitter, Output } from '@angular/core';
import { RouterModule } from '@angular/router';

@Component({
  selector: 'dl-bottom-nav',
  standalone: true,
  imports: [RouterModule],
  template: `
    <nav class="lg:hidden fixed bottom-0 left-0 right-0 border-t bg-white">
      <div class="flex justify-around py-3 text-sm">
        <a routerLink="/app/dashboard" routerLinkActive="font-semibold">Home</a>
        <a routerLink="/app/receipts" routerLinkActive="font-semibold">Receipts</a>
        <a routerLink="/app/ledger" routerLinkActive="font-semibold">Ledger</a>
        <button class="underline" (click)="ai.emit()">AI</button>
      </div>
    </nav>
  `
})
export class BottomNavComponent {
  @Output() ai = new EventEmitter<void>();
}
