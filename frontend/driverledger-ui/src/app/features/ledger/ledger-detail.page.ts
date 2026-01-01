import { Component } from '@angular/core';
import { PageHeaderComponent } from '../../shared/ui/page-header/page-header.component';

@Component({
  selector: 'dl-ledger-detail-page',
  standalone: true,
  imports: [PageHeaderComponent],
  template:  `
     <dl-page-header title="Ledger Entry" subtitle="Read-only detail (M1)." />
    <div class="border rounded-xl p-4 text-sm opacity-70">
      Ledger detail scaffold. Next: wire to GET ledger & entryId.
    </div>
  `
})
export class LedgerDetailPage {}
