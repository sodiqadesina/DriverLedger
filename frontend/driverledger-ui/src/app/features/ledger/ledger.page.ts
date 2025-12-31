import { Component } from '@angular/core';
import { PageHeaderComponent } from '../../shared/ui/page-header/page-header.component';

@Component({
  selector: 'dl-ledger-page',
  standalone: true,
  imports: [PageHeaderComponent],
  template: `
    <dl-page-header title="Ledger" subtitle="Read-only projection (M1)." />
    <div class="border rounded-xl p-4 text-sm opacity-70">
      Ledger UI scaffold. Next: wire to GET /ledger and render list + details.
    </div>
  `
})
export class LedgerPage {}
