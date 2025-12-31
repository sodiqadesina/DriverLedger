import { Component, EventEmitter, Input, Output } from '@angular/core';
import { DrawerComponent } from '../../../shared/ui/drawer/drawer.component';

@Component({
  selector: 'dl-ai-drawer',
  standalone: true,
  imports: [DrawerComponent],
  template: `
    <dl-drawer [open]="open" title="AI Assistant" (close)="close.emit()">
      <p class="text-sm opacity-70">
        Placeholder. Later this becomes the Driver CFO assistant wired to Azure OpenAI tool-calling.
      </p>

      <div class="mt-4 border rounded p-3 text-sm">
        <div class="opacity-70">Try later:</div>
        <ul class="list-disc ml-5">
          <li>“Why is this receipt on HOLD?”</li>
          <li>“Show me what changed in my live statement this month.”</li>
        </ul>
      </div>
    </dl-drawer>
  `
})
export class AiDrawerComponent {
  @Input() open = false;
  @Output() close = new EventEmitter<void>();
}
