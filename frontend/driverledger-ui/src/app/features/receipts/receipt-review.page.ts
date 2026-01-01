import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, RouterModule } from '@angular/router';
import { PageHeaderComponent } from '../../shared/ui/page-header/page-header.component';
import { FormsModule } from '@angular/forms';
import { ReceiptsFacade } from './receipts.facade';

@Component({
  selector: 'dl-receipt-review',
  standalone: true,
  imports: [CommonModule, RouterModule, FormsModule, PageHeaderComponent],
  template: `
    <dl-page-header title="Receipt Review" subtitle="Resolve HOLD receipts (backend workflow)." />

    <div class="border rounded-xl p-4">
      <div class="text-sm opacity-70">Receipt ID</div>
      <div class="font-semibold">{{ receiptId }}</div>

      <div class="mt-4 text-sm opacity-70">Resolution JSON</div>
      <textarea class="border rounded w-full p-2 mt-2 h-40" [(ngModel)]="resolutionJson"></textarea>

      <div class="mt-4 flex gap-2">
        <button class="border rounded p-2 font-semibold"
                (click)="resolve(true)">
          Resubmit
        </button>

        <button class="border rounded p-2"
                (click)="resolve(false)">
          Mark Ready (no resubmit)
        </button>

        <a class="underline p-2" routerLink="/app/receipts">Back</a>
      </div>

      <div *ngIf="facade.error()" class="mt-3 border rounded p-2 text-sm">
        {{ facade.error() }}
      </div>
    </div>
  `
})
export class ReceiptReviewPage {
  private readonly route = inject(ActivatedRoute);
  public readonly facade = inject(ReceiptsFacade);

  receiptId = this.route.snapshot.paramMap.get('id') ?? '';
  resolutionJson = '{}';

  resolve(resubmit: boolean) {
    this.facade.resolveHold(this.receiptId, this.resolutionJson, resubmit);
  }
}
