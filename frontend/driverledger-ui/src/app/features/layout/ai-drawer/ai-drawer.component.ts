import { Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';

@Component({
  selector: 'dl-ai-drawer',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './ai-drawer.component.html',
  styleUrl: './ai-drawer.component.css',
})
export class AiDrawerComponent {
  @Input() open = false;
  @Output() close = new EventEmitter<void>();
}
