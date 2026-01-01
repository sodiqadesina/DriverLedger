import { Component } from '@angular/core';
import { ReceiptsFacade } from './receipts.facade';

@Component({
  selector: 'dl-receipt-upload',
  standalone: true,
  template: `
    <div class="border rounded-xl p-4">
      <div class="font-semibold">Upload Receipt</div>
      <p class="text-sm opacity-70">Upload a document. The backend will process it asynchronously.</p>

      <input class="mt-3" type="file" (change)="onFile($event)" />

      <div class="text-sm opacity-70 mt-2">
        Flow: upload → create draft → submit → processing.
      </div>
    </div>
  `
})
export class ReceiptUploadComponent {
  constructor(private readonly facade: ReceiptsFacade) {}

  onFile(evt: Event) {
    const input = evt.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;

    this.facade.uploadAndSubmit(file);
    input.value = '';
  }
}
