import { Component } from '@angular/core';
import { PageHeaderComponent } from '../../shared/ui/page-header/page-header.component';

@Component({
  selector: 'dl-live-statement-page',
  standalone: true,
  imports: [PageHeaderComponent],
  template: `
    <dl-page-header title="Live Statement" subtitle="Read-only statement snapshot (M1)." />
    <div class="border rounded-xl p-4 text-sm opacity-70">
      Live statement scaffold. Next: wire to GET /live-statement.
    </div>
  `
})
export class LiveStatementPage {}
