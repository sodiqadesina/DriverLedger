import { Injectable, signal } from '@angular/core';
import { ReceiptsApi } from './receipts.api';
import { ReceiptListItemDto } from '../../shared/models/receipts.models';
import { finalize, switchMap } from 'rxjs/operators';
import { of } from 'rxjs';

@Injectable({ providedIn: 'root' })
export class ReceiptsFacade {
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly receipts = signal<ReceiptListItemDto[]>([]);

  constructor(private readonly api: ReceiptsApi) {}

  refresh() {
    this.loading.set(true);
    this.error.set(null);

    this.api.list().pipe(finalize(() => this.loading.set(false))).subscribe({
      next: (items) => this.receipts.set(items),
      error: (e) => this.error.set(e?.error?.message ?? 'Failed to load receipts.'),
    });
  }

  uploadAndSubmit(file: File) {
    this.loading.set(true);
    this.error.set(null);

    this.api.uploadFile(file).pipe(
      switchMap((up) => this.api.createReceipt({ fileObjectId: up.fileObjectId })),
      switchMap((created) => this.api.submitReceipt(created.receiptId)),
      switchMap(() => this.api.list()),
      finalize(() => this.loading.set(false)),
    ).subscribe({
      next: (items) => this.receipts.set(items),
      error: (e) => this.error.set(e?.error?.message ?? 'Upload failed.'),
    });
  }

  resolveHold(receiptId: string, resolutionJson: string, resubmit: boolean) {
    this.loading.set(true);
    this.error.set(null);

    this.api.resolveHold(receiptId, { resolutionJson, resubmit }).pipe(
      switchMap(() => this.api.list()),
      finalize(() => this.loading.set(false)),
    ).subscribe({
      next: (items) => this.receipts.set(items),
      error: (e) => this.error.set(e?.error?.message ?? 'Resolve failed.'),
    });
  }
}
