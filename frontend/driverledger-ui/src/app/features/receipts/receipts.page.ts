import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { PageHeaderComponent } from '../../shared/ui/page-header/page-header.component';
import { ReceiptUploadComponent } from './receipt-upload.component';
import { ReceiptListComponent } from './receipt-list.component';
import { ReceiptsFacade } from './receipts.facade';

@Component({
  selector: 'dl-receipts-page',
  standalone: true,
  imports: [CommonModule, PageHeaderComponent, ReceiptUploadComponent, ReceiptListComponent],
  template: `
    <dl-page-header title="Receipts" subtitle="Upload documents and resolve HOLD receipts." />

    <div class="grid gap-3">
      <dl-receipt-upload />

      <div *ngIf="facade.error()" class="border rounded p-3 text-sm">{{ facade.error() }}</div>
      <div *ngIf="facade.loading()" class="text-sm opacity-70">Loading...</div>

      <dl-receipt-list [items]="facade.receipts()" />
    </div>
  `
})
export class ReceiptsPage implements OnInit {
  constructor(public readonly facade: ReceiptsFacade) {}
  ngOnInit() { this.facade.refresh(); }
}
