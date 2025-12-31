import { Component, Input } from '@angular/core';
import { RouterModule } from '@angular/router';
import { ReceiptListItemDto } from '../../shared/models/receipts.models';
import { StatusPillComponent } from '../../shared/ui/status-pill/status-pill.component';
import { CommonModule } from '@angular/common';

@Component({
  selector: 'dl-receipt-list',
  standalone: true,
  imports: [CommonModule, RouterModule, StatusPillComponent],
  template: `
    <div class="border rounded-xl p-4">
      <div class="font-semibold mb-3">Receipts</div>

      <div *ngIf="items.length === 0" class="text-sm opacity-70">No receipts yet.</div>

      <div class="flex flex-col gap-2">
        <div *ngFor="let r of items" class="border rounded p-3 flex items-center justify-between gap-3">
          <div>
            <div class="text-sm font-semibold">{{ r.id }}</div>
            <div class="text-xs opacity-70">Created: {{ r.createdAt }}</div>
          </div>

          <div class="flex items-center gap-2">
            <dl-status-pill [label]="r.status" />
            <a *ngIf="isHold(r.status)"
               class="underline text-sm"
               [routerLink]="['/app/receipts', r.id, 'review']">
              Review
            </a>
          </div>
        </div>
      </div>
    </div>
  `
})
export class ReceiptListComponent {
  @Input({ required: true }) items!: ReceiptListItemDto[];

  isHold(status: string) {
    return status.toLowerCase().includes('hold');
  }
}
